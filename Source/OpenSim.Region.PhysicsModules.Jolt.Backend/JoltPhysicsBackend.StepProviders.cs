/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The step listener and the per-body force providers (see StepForceProvider.cs).
//
// ONE listener per physics system. It is registered with Jolt when the first provider attaches and removed when the
// last one detaches (or the body of the last one is removed, or the backend is disposed). A registered listener makes
// every Update run its full pipeline even with nothing awake (PhysicsSystem.cpp, the early-out tests that no step
// listener is registered), so a region with no providers must have none registered.
//
// Attach, detach and body removal take _simLock, which Step holds for the whole Update. So the listener is added and
// removed only between Updates (Jolt's own AddStepListener would otherwise wait for the Update's listener mutex), and
// the provider list is never changed while OnStep reads it. OnStep takes no lock: it reads a copy-on-write array.
//
// The listener's OnStep reaches joltc, which looks the system up in a process-wide map (joltc.cpp, OnStep). Creating
// or destroying any physics system holds every job pool's gate for that reason (AllPoolGates).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JoltPhysicsSharp;

namespace OpenSim.Region.PhysicsModules.Jolt.Backend
{
    public sealed partial class JoltPhysicsBackend
    {
        private sealed class ProviderEntry
        {
            public readonly uint Handle;   // the body's handle in this backend
            public readonly BodyID Id;
            public readonly IStepForceProvider Provider;

            public ProviderEntry(uint handle, BodyID id, IStepForceProvider provider)
            {
                Handle = handle;
                Id = id;
                Provider = provider;
            }
        }

        private sealed class ForceStepListener : PhysicsStepListener
        {
            private readonly JoltPhysicsBackend _owner;
            public ForceStepListener(JoltPhysicsBackend owner) => _owner = owner;

            protected override void OnStep(in PhysicsStepListenerContext context) => _owner.RunStepProviders(in context);
        }

        private static readonly ProviderEntry[] s_noProviders = Array.Empty<ProviderEntry>();

        // Written under _simLock (attach, detach, body removal, Dispose); read by OnStep with no lock.
        private ProviderEntry[] _stepProviders = s_noProviders;
        private ForceStepListener? _stepListener;
        private BodyInterface _bodyInterfaceNoLock;   // set once in Initialize; the only body interface OnStep may use
        private long _stepProviderFaults;
        private Exception? _lastStepProviderFault;

        // TEST-ONLY. Keep the listener registered with no provider attached, so the cost of an empty listener can be measured.
        private bool _keepListenerForTest;
        // TEST-ONLY. Time every OnStep call in Stopwatch ticks, and count them.
        internal bool TimeStepListenerForTest;
        private long _onStepTicks;
        private long _onStepCalls;

        /// <summary>True while this region's physics system has a step listener registered.</summary>
        internal bool HasStepListener { get { lock (_simLock) return _stepListener != null; } }

        /// <summary>Providers attached now.</summary>
        public int StepForceProviderCount => Volatile.Read(ref _stepProviders).Length;

        /// <summary>Provider calls that threw; the exception is counted and swallowed so one provider cannot stop the step.</summary>
        public long StepForceProviderFaults => Interlocked.Read(ref _stepProviderFaults);

        /// <summary>The last exception a provider threw, for the caller to log; null when none.</summary>
        public Exception? LastStepForceProviderFault => Volatile.Read(ref _lastStepProviderFault);

        internal long OnStepTicksForTest => Interlocked.Read(ref _onStepTicks);
        internal long OnStepCallsForTest => Interlocked.Read(ref _onStepCalls);

        public IDisposable AttachStepForceProvider(BodyId body, IStepForceProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));
            lock (_simLock)
            {
                if (_disposed || _system == null)
                    throw new ObjectDisposedException(nameof(JoltPhysicsBackend));
                if (!_bodies.TryGet(body.Value, out JoltBodyRecord rec))
                    throw new ArgumentException($"AttachStepForceProvider: {body} is not a live body.", nameof(body));

                var entry = new ProviderEntry(body.Value, new BodyID(rec.NativeBodyId), provider);
                ProviderEntry[] old = _stepProviders;
                var next = new ProviderEntry[old.Length + 1];
                Array.Copy(old, next, old.Length);
                next[old.Length] = entry;
                Volatile.Write(ref _stepProviders, next);
                EnsureStepListener();
                return new ProviderAttachment(this, entry);
            }
        }

        private sealed class ProviderAttachment : IDisposable
        {
            private JoltPhysicsBackend? _owner;
            private readonly ProviderEntry _entry;

            public ProviderAttachment(JoltPhysicsBackend owner, ProviderEntry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public void Dispose()
            {
                JoltPhysicsBackend? owner = Interlocked.Exchange(ref _owner, null);
                owner?.DetachStepForceProvider(_entry);
            }
        }

        private void DetachStepForceProvider(ProviderEntry entry)
        {
            lock (_simLock)
            {
                if (_disposed)
                    return;   // Dispose already took the listener off and cleared the list
                RemoveProviders(e => ReferenceEquals(e, entry));
            }
        }

        // Under _simLock. Drops the providers `drop` selects and takes the listener off when none is left.
        private void RemoveProviders(Func<ProviderEntry, bool> drop)
        {
            ProviderEntry[] old = _stepProviders;
            var kept = new List<ProviderEntry>(old.Length);
            foreach (ProviderEntry e in old)
                if (!drop(e))
                    kept.Add(e);
            if (kept.Count == old.Length)
                return;
            Volatile.Write(ref _stepProviders, kept.Count == 0 ? s_noProviders : kept.ToArray());
            if (kept.Count == 0 && !_keepListenerForTest)
                DropStepListener();
        }

        // Under _simLock: a body that is going away takes its providers with it.
        private void DropProvidersOfBody(uint handle)
        {
            if (_stepProviders.Length > 0)
                RemoveProviders(e => e.Handle == handle);
        }

        private void EnsureStepListener()
        {
            if (_stepListener != null || _system == null)
                return;
            var listener = new ForceStepListener(this);
            _system.AddStepListener(listener);
            _stepListener = listener;
        }

        private void DropStepListener()
        {
            ForceStepListener? listener = _stepListener;
            if (listener == null)
                return;
            _stepListener = null;
            _system?.RemoveStepListener(listener);
            listener.Dispose();
        }

        /// <summary>TEST-ONLY: register the listener with no provider, and keep it registered when the last provider detaches.</summary>
        internal void KeepStepListenerForTest()
        {
            lock (_simLock)
            {
                _keepListenerForTest = true;
                EnsureStepListener();
            }
        }

        // Under _simLock, from DisposeNative before the system is destroyed.
        private void ReleaseStepProviders()
        {
            Volatile.Write(ref _stepProviders, s_noProviders);
            DropStepListener();
        }

        // Runs inside Jolt's step, on a worker or the stepping thread, with every body locked. No lock, no allocation.
        private void RunStepProviders(in PhysicsStepListenerContext context)
        {
            long started = TimeStepListenerForTest ? Stopwatch.GetTimestamp() : 0;
            ProviderEntry[] list = Volatile.Read(ref _stepProviders);
            if (list.Length > 0)
            {
                BodyInterface bodies = _bodyInterfaceNoLock;
                var step = new StepInfo(context.DeltaTime, context.IsFirstStep, context.IsLastStep);
                for (int i = 0; i < list.Length; i++)
                {
                    ProviderEntry e = list[i];
                    try
                    {
                        if (bodies.IsActive(e.Id))
                            e.Provider.OnStep(new StepBody(bodies, e.Id), in step);
                    }
                    catch (Exception ex)
                    {
                        // An exception out of a native callback would end the process.
                        Interlocked.Increment(ref _stepProviderFaults);
                        Volatile.Write(ref _lastStepProviderFault, ex);
                    }
                }
            }
            if (TimeStepListenerForTest)
            {
                Interlocked.Add(ref _onStepTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref _onStepCalls);
            }
        }
    }
}

/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// Several regions on one job pool are safe on a joltc whose regions share one TempAllocator (the stock
/// JoltPhysics.Native) only because of two rules in the backend:
/// <list type="bullet">
/// <item>the pool gate rule: every call that draws on the allocator (PhysicsSystem::Update and
/// CharacterVirtual::ExtendedUpdate) happens inside the job pool's gate, which admits one Step at a time;</item>
/// <item>the maximum depth rule: CharacterVirtual::SetShape, the one allocator entry point called outside the gate,
/// always passes the maximum penetration depth, at which Jolt swaps the shape without testing it and touches no
/// allocator (Jolt CharacterVirtual.cpp:1504, v5.4.0).</item>
/// </list>
/// A break of either aborts the process from native code only when two regions happen to collide, so these tests
/// check the rules themselves: the compiled calls into the seven allocator entry points, the depth the shape call
/// passes, and the backend's own gate check. In the native serial collection: the runtime tests step a real backend
/// and turn on <see cref="JoltPhysicsBackend.AllocatorOwnerCheck"/>, a process-wide switch that only adds checks
/// (nothing else in the suite turns it off while these run).
/// </summary>
[Collection(JoltNativeSerial.Name)]
public class ShapeAndAllocatorRuleTests
{
    // The binding methods that reach a PhysicsSystem's TempAllocator (joltc's seven consuming sites).
    private static readonly (string type, string method)[] AllocatorEntryPoints =
    {
        ("JoltPhysicsSharp.PhysicsSystem", "Update"),
        ("JoltPhysicsSharp.CharacterVirtual", "Update"),
        ("JoltPhysicsSharp.CharacterVirtual", "ExtendedUpdate"),
        ("JoltPhysicsSharp.CharacterVirtual", "RefreshContacts"),
        ("JoltPhysicsSharp.CharacterVirtual", "WalkStairs"),
        ("JoltPhysicsSharp.CharacterVirtual", "StickToFloor"),
        ("JoltPhysicsSharp.CharacterVirtual", "SetShape"),
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // One IL instruction: its opcode and its operand (a metadata token, a float, or 0).
    private readonly record struct Instruction(OpCode Op, int Token, float Single);

    private static List<Instruction> Decode(MethodBase method)
    {
        var list = new List<Instruction>();
        byte[] il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return list;
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            OpCode op = OpCodesByValue[value];
            int token = 0;
            float single = 0f;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.ShortInlineR:
                    single = BitConverter.ToSingle(il, i);
                    i += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default:   // tokens, branch targets, InlineI
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
            }
            list.Add(new Instruction(op, token, single));
        }
        return list;
    }

    private static IEnumerable<MethodBase> AllMethods(Assembly assembly)
        => assembly.GetTypes().SelectMany(t => t.GetMethods(Everything).Cast<MethodBase>().Concat(t.GetConstructors(Everything)));

    private static MethodBase Resolve(MethodBase caller, int token)
    {
        Type t = caller.DeclaringType;
        try
        {
            return caller.Module.ResolveMethod(token,
                t is { IsGenericType: true } ? t.GetGenericArguments() : null,
                caller.IsGenericMethod ? caller.GetGenericArguments() : null);
        }
        catch (ArgumentException) { return null; }
    }

    // Every method in the Jolt assemblies that calls one of the allocator entry points, by entry point.
    private static Dictionary<string, SortedSet<string>> CallersOfAllocatorEntryPoints()
    {
        var found = AllocatorEntryPoints.ToDictionary(e => $"{e.type}.{e.method}", _ => new SortedSet<string>());
        Assembly[] assemblies =
        {
            typeof(JoltPhysicsBackend).Assembly,
            typeof(JoltScene).Assembly,
            Assembly.Load("OpenSim.Region.PhysicsModules.Jolt.Vehicles"),
        };
        foreach (Assembly assembly in assemblies)
            foreach (MethodBase caller in AllMethods(assembly))
                foreach (Instruction ins in Decode(caller))
                {
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Newobj && ins.Op != OpCodes.Ldftn)
                        continue;
                    MethodBase callee = Resolve(caller, ins.Token);
                    string key = $"{callee?.DeclaringType?.FullName}.{callee?.Name}";
                    if (callee != null && found.TryGetValue(key, out SortedSet<string> callers))
                        callers.Add($"{caller.DeclaringType!.Name}.{caller.Name}");
                }
        return found;
    }

    // The pool gate rule and the maximum depth rule cover exactly these calls. A new caller of any allocator entry
    // point fails here, so whoever adds it has to place it under one of the rules (and add it to this list).
    [Fact]
    public void Every_call_into_an_allocator_entry_point_is_one_the_rules_cover()
    {
        Dictionary<string, SortedSet<string>> callers = CallersOfAllocatorEntryPoints();
        var expected = new Dictionary<string, string[]>
        {
            ["JoltPhysicsSharp.PhysicsSystem.Update"] = new[] { "JoltPhysicsBackend.StepLocked" },                     // gate
            ["JoltPhysicsSharp.CharacterVirtual.ExtendedUpdate"] = new[] { "JoltPhysicsBackend.StepCharacter" },       // gate
            ["JoltPhysicsSharp.CharacterVirtual.SetShape"] = new[] { "JoltPhysicsBackend.SetShapeWithoutScratch" },    // depth
            ["JoltPhysicsSharp.CharacterVirtual.Update"] = Array.Empty<string>(),
            ["JoltPhysicsSharp.CharacterVirtual.RefreshContacts"] = new[] { "JoltPhysicsBackend.StepCharacter" },   // gate
            ["JoltPhysicsSharp.CharacterVirtual.WalkStairs"] = Array.Empty<string>(),
            ["JoltPhysicsSharp.CharacterVirtual.StickToFloor"] = Array.Empty<string>(),
        };
        foreach ((string entry, string[] want) in expected)
            Assert.True(want.SequenceEqual(callers[entry]),
                $"{entry} is called from [{string.Join(", ", callers[entry])}], expected [{string.Join(", ", want)}]");
    }

    // The pool gate rule's calls: StepLocked runs only from Step, which holds the gate, and StepCharacter only from
    // StepLocked.
    [Fact]
    public void The_gated_calls_are_reached_only_from_the_step()
    {
        var callersOf = new Dictionary<string, SortedSet<string>> { ["StepLocked"] = new(), ["StepCharacter"] = new() };
        Assembly[] assemblies = { typeof(JoltPhysicsBackend).Assembly, typeof(JoltScene).Assembly };
        foreach (Assembly assembly in assemblies)
            foreach (MethodBase caller in AllMethods(assembly))
                foreach (Instruction ins in Decode(caller))
                {
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Ldftn)
                        continue;
                    MethodBase callee = Resolve(caller, ins.Token);
                    if (callee?.DeclaringType == typeof(JoltPhysicsBackend) && callersOf.TryGetValue(callee.Name, out SortedSet<string> set))
                        set.Add($"{caller.DeclaringType!.Name}.{caller.Name}");
                }
        Assert.Equal(new[] { "JoltPhysicsBackend.Step" }, callersOf["StepLocked"]);
        Assert.Equal(new[] { "JoltPhysicsBackend.StepLocked" }, callersOf["StepCharacter"]);
    }

    // The maximum depth rule: the one SetShape call passes float.MaxValue, a constant no caller can change.
    [Fact]
    public void The_character_shape_call_always_passes_the_maximum_penetration_depth()
    {
        Assert.Equal(float.MaxValue, JoltPhysicsBackend.CharacterShapeMaxPenetrationDepth);

        MethodInfo helper = typeof(JoltPhysicsBackend).GetMethod("SetShapeWithoutScratch", Everything)!;
        Assert.NotNull(helper);
        Assert.DoesNotContain(helper.GetParameters(), p => p.ParameterType == typeof(float));   // the depth is not passed in

        List<Instruction> il = Decode(helper);
        float[] constants = il.Where(x => x.Op == OpCodes.Ldc_R4).Select(x => x.Single).ToArray();
        // SetShape(deltaTime 0, shape, maxPenetrationDepth, ...): the only float constants are the unused delta time
        // and the depth.
        Assert.Equal(new[] { 0f, float.MaxValue }, constants);
        Assert.DoesNotContain(il, x => x.Op == OpCodes.Ldsfld || x.Op == OpCodes.Ldfld);   // no field can supply a depth
    }

    // Only the backend uses a step listener: the stock joltc keeps its map of physics systems (s_PhysicsSystems) without a
    // lock, and only its step listener callback reads that map during a step. The backend creates and destroys every
    // physics system with all pool gates held, so no Update is running to read the map; any other code that registered a
    // listener would not have that protection. So the only listener type is the backend's own ForceStepListener, and the
    // only code that adds, removes or creates one is the backend (no other Jolt assembly, no binding object's step
    // listener view such as a vehicle constraint's AsPhysicsStepListener).
    [Fact]
    public void Only_the_backend_uses_a_step_listener()
    {
        Assembly[] assemblies =
        {
            typeof(JoltPhysicsBackend).Assembly,
            typeof(JoltScene).Assembly,
            Assembly.Load("OpenSim.Region.PhysicsModules.Jolt.Vehicles"),
        };
        // The names this looks for are the binding's own: a scan for a name the binding lacks would pass vacuously.
        Assert.Contains(typeof(JoltPhysicsSharp.PhysicsSystem).GetMethods(), m => m.Name.Contains("StepListener", StringComparison.Ordinal));
        Assert.NotNull(typeof(JoltPhysicsSharp.PhysicsSystem).Assembly.GetType("JoltPhysicsSharp.IPhysicsStepListener"));

        var listenerTypes = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.GetInterfaces().Any(i => i.FullName == "JoltPhysicsSharp.IPhysicsStepListener"))
            .Select(t => t.FullName).ToArray();
        Assert.Equal(new[] { "OpenSim.Region.PhysicsModules.Jolt.Backend.JoltPhysicsBackend+ForceStepListener" }, listenerTypes);

        var calls = new SortedSet<string>();
        foreach (Assembly assembly in assemblies)
            foreach (MethodBase caller in AllMethods(assembly))
                foreach (Instruction ins in Decode(caller))
                {
                    if (ins.Op != OpCodes.Call && ins.Op != OpCodes.Callvirt && ins.Op != OpCodes.Newobj && ins.Op != OpCodes.Ldftn)
                        continue;
                    MethodBase callee = Resolve(caller, ins.Token);
                    if (caller.DeclaringType?.FullName?.StartsWith("OpenSim.Region.PhysicsModules.Jolt.Backend.JoltPhysicsBackend", StringComparison.Ordinal) == true)
                        continue;   // the backend's own use
                    if (callee?.DeclaringType?.Namespace == "JoltPhysicsSharp" && callee.Name.Contains("StepListener", StringComparison.Ordinal))
                        calls.Add($"{caller.DeclaringType!.Name}.{caller.Name} -> {callee.DeclaringType.Name}.{callee.Name}");
                }
        Assert.Empty(calls);
    }

    // ------------------------------------------------------------------ the backend's own gate check

    private static CharacterDesc Avatar(Vector3 at) => new()
    {
        Position = at,
        Orientation = Quaternion.Identity,
        CapsuleHalfHeight = 0.45f,
        CapsuleRadius = 0.30f,
        Mass = 80f,
        Friction = 0.5f,
        MaxSlopeAngle = 1.0f,
        StepHeight = 0.45f,
        PushStrength = 1f,
        UserData = 7u,
    };

    private static void WithAllocatorCheck(Action body)
    {
        bool before = JoltPhysicsBackend.AllocatorOwnerCheck;
        JoltPhysicsBackend.AllocatorOwnerCheck = true;
        try { body(); }
        finally { JoltPhysicsBackend.AllocatorOwnerCheck = before; }
    }

    // With the check on, a step with an avatar walking and a body falling runs Update and ExtendedUpdate inside the
    // gate, and an avatar resize (SetShape, outside the gate) passes too.
    [Fact]
    public void The_steps_allocator_calls_hold_the_pool_gate_and_a_resize_needs_none()
    {
        WithAllocatorCheck(() =>
        {
            using var t = new JoltTestBackend();
            t.Ground();
            t.Dynamic(t.B.CreateBoxShape(new Vector3(0.5f)), new Vector3(2f, 0f, 3f));
            CharacterId avatar = t.B.CreateCharacter(Avatar(new Vector3(0f, 0f, 1.2f)));
            for (int i = 0; i < 20; i++)
            {
                t.B.SetCharacterMovement(avatar, new Vector3(1f, 0f, 0f), false, false);
                t.Step();
                if (i == 10)
                    t.B.SetCharacterShape(avatar, 0.6f, 0.3f);
            }
        });
    }

    // A call that draws on the allocator from outside the gate is refused by the check.
    [Fact]
    public void An_allocator_call_outside_the_pool_gate_is_refused()
    {
        WithAllocatorCheck(() =>
        {
            using var t = new JoltTestBackend();
            var e = Assert.Throws<InvalidOperationException>(() => t.B.EnterGatedAllocatorSiteForTest("test site"));
            Assert.Contains("outside the job pool gate", e.Message);
        });
    }
}

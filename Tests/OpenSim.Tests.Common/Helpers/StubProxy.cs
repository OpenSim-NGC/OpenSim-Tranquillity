/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;

namespace OpenSim.Tests.Common;

/// <summary>
/// Implements any interface at run time. Every member returns the default value of its return type unless the
/// handler answers it, so a test only writes the one or two calls the code under test makes.
/// </summary>
public class StubProxy : DispatchProxy
{
    private Func<MethodInfo, object[], (bool Handled, object Result)> m_handler;

    public static T Create<T>(Func<MethodInfo, object[], (bool Handled, object Result)> handler = null) where T : class
    {
        T proxy = Create<T, StubProxy>();
        ((StubProxy)(object)proxy).m_handler = handler;
        return proxy;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (m_handler != null)
        {
            var (handled, result) = m_handler(targetMethod, args);
            if (handled)
                return result;
        }

        Type returnType = targetMethod.ReturnType;
        if (returnType == typeof(void))
            return null;
        if (returnType.IsArray)
            return Array.CreateInstance(returnType.GetElementType(), 0);
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

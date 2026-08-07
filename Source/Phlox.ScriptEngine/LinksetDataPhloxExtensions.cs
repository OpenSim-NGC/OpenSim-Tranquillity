/* Copyright (c) 2026 Legion Grid / Tranquillity integration
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.Framework.Scenes;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// SL-shaped binding for Phlox over Tranquillity's native <see cref="LinksetData"/> store.
    ///
    /// Phlox's <c>llLinksetData*</c> implementation (in LSLSystemAPI) was written against a
    /// Legion-shaped API (Free/Get/Remove/AddOrUpdate/...). Tranquillity's native LinksetData
    /// provides the same capabilities under different names and with non-SL return codes
    /// (-1/1/2), which Tranquillity's YEngine translates to SL codes in its own LSL_Api.
    ///
    /// These extension methods are the Phlox-side seam: they forward to Tranquillity's native
    /// methods (so the data lives on, and persists through, the root SceneObjectPart) and
    /// translate the native return codes into SL status codes — mirroring YEngine exactly so
    /// both engines are script-observably identical. Tranquillity's LinksetData class itself is
    /// left byte-unchanged.
    /// </summary>
    internal static class LinksetDataPhloxExtensions
    {
        // SL status codes (== ScriptBaseClass.LINKSETDATA_* / Phlox's local consts)
        private const int SL_OK = 0;
        private const int SL_EMEMORY = 1;
        private const int SL_EPROTECTED = 3;
        private const int SL_NOTFOUND = 4;
        private const int SL_NOUPDATE = 5;

        public static int Free(this LinksetData lsd) => lsd.BytesFree;

        public static string Get(this LinksetData lsd, string key, string pass = "")
            => lsd.ReadLinksetData(key, pass);

        public static int AddOrUpdate(this LinksetData lsd, string key, string value, string pass = "")
        {
            // native AddOrUpdateLinksetDataKey: 0=OK, 1=over limit, 2=value unchanged
            // (callers pre-check empty key), -1=wrong password.  Map to SL (see YEngine).
            int ret = lsd.AddOrUpdateLinksetDataKey(key, value, pass);
            return ret switch
            {
                0 => SL_OK,
                1 => SL_EMEMORY,
                2 => SL_NOUPDATE,
                -1 => SL_EPROTECTED,
                _ => ret
            };
        }

        public static int Remove(this LinksetData lsd, string key, string pass = "")
        {
            // native DeleteLinksetDataKey: 0=OK, 1=wrong password, -1=no such key.
            int ret = lsd.DeleteLinksetDataKey(key, pass);
            return ret switch
            {
                0 => SL_OK,
                1 => SL_EPROTECTED,
                -1 => SL_NOTFOUND,
                _ => ret
            };
        }

        public static int CountByPattern(this LinksetData lsd, string pattern)
            => lsd.LinksetDataCountMatches(pattern);

        public static string[] ListKeys(this LinksetData lsd, int start, int count)
            => lsd.GetLinksetDataSubList(start, count);

        // Note: name matches Phlox's existing (typo'd) call site "ListKeysByPatttern".
        public static string[] ListKeysByPatttern(this LinksetData lsd, string pattern, int start, int count)
            => lsd.FindLinksetDataKeys(pattern, start, count);

        public static string[] RemoveByPattern(this LinksetData lsd, string pattern, string pass, out int notDeleted)
        {
            string[] deleted = lsd.LinksetDataMultiDelete(pattern, pass, out int _, out int nd);
            notDeleted = nd;
            return deleted;
        }
    }
}

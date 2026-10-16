/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Text;
public static class CP {
  public static void Main(string[] a) {
    StringBuilder sb = new StringBuilder();
    for (int c = 0; c < 0x10000; c++) {
      char ch = (char)c;
      sb.Append((Char.IsLetterOrDigit(ch) ? 1 : 0) + (Char.IsDigit(ch) ? 2 : 0));
    }
    System.IO.File.WriteAllText(a[0], sb.ToString());
  }
}

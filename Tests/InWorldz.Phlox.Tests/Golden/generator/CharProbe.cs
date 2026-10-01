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

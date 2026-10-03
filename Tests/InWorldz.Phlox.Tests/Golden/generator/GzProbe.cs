// Halcyon's gzipDecompress body (LSLSystemAPI.cs:16418-16427) on raw bytes, for the fuzz corpus.
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.1")]
public static class GzProbe {
  public static void Main(string[] a) {
    using (StreamWriter w = new StreamWriter(a[1], false, new UTF8Encoding(false))) {
      w.NewLine = "\n";
      foreach (string line in File.ReadAllLines(a[0])) {
        byte[] bytes = new byte[line.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(line.Substring(i * 2, 2), 16);
        string r;
        try {
          using (var msi = new MemoryStream(bytes))
          using (var mso = new MemoryStream()) {
            using (var gs = new GZipStream(msi, CompressionMode.Decompress)) { gs.CopyTo(mso); }
            r = "ok " + BitConverter.ToString(mso.ToArray()).Replace("-", "").ToLowerInvariant();
          }
        } catch (Exception e) { r = "ex " + e.GetType().FullName + ": " + e.Message; }
        w.WriteLine(r);
      }
    }
  }
}

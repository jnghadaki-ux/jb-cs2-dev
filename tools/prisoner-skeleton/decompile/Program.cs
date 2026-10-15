using ValveResourceFormat;
using ValveResourceFormat.IO;
var outDir = args[1];
using var res = new Resource();
res.Read(args[0]);
using var cf = FileExtract.Extract(res, new NullFileLoader(), null);
Directory.CreateDirectory(outDir);
File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(cf.FileName ?? "model.vmdl")), cf.Data);
Console.WriteLine("main: " + cf.FileName);
foreach (var sf in cf.SubFiles)
{
    var p = Path.Combine(outDir, sf.FileName.Replace('\\', '/'));
    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    File.WriteAllBytes(p, sf.Extract());
    Console.WriteLine("sub: " + sf.FileName);
}

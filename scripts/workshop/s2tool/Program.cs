// s2tool: small ValveResourceFormat front end for scripts/workshop (store_models.py).
//   s2tool info    <root> <file_c>            resource type, external references, model bones
//   s2tool skel    <root> <vmdl_c> <out.json> model skeleton (local position + rotation per bone)
//   s2tool glb     <root> <vmdl_c> <out.glb>  model as glTF binary with skin and materials
//   s2tool extract <root> <file_c> <outdir>   decompile (vmdl -> vmdl + dmx, vmat -> vmat + png, vtex -> png)
// <root> is the folder that holds the game paths (characters/..., materials/...); files are relative to it.
using System.Globalization;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: s2tool info|skel|glb|extract <root> <file_c> [out]");
    return 2;
}

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var (mode, root, rel) = (args[0], args[1], args[2]);
var full = Path.Combine(root, rel);
using var loader = new GameFileLoader(null, full);
loader.AddDiskPathToSearch(root);
using var res = new Resource { FileName = full };
res.Read(full);

switch (mode)
{
    case "info":
        Console.WriteLine($"type {res.ResourceType}");
        foreach (var r in res.ExternalReferences?.ResourceRefInfoList ?? new()) Console.WriteLine($"ref {r.Name}");
        if (res.DataBlock is Model info)
            foreach (var b in info.Skeleton.Bones) Console.WriteLine($"bone {b.Index} {b.Name} parent={b.Parent?.Name ?? "-"}");
        break;

    case "skel":
    {
        var bones = ((Model)res.DataBlock!).Skeleton.Bones;
        var lines = bones.Select(b =>
            $"{{\"name\": \"{b.Name}\", \"parent\": \"{b.Parent?.Name ?? ""}\", " +
            $"\"pos\": [{b.Position.X:R}, {b.Position.Y:R}, {b.Position.Z:R}], " +
            $"\"quat\": [{b.Angle.X:R}, {b.Angle.Y:R}, {b.Angle.Z:R}, {b.Angle.W:R}]}}");
        File.WriteAllText(args[3], "[\n" + string.Join(",\n", lines) + "\n]\n");
        break;
    }

    case "glb":
    {
        var exporter = new GltfModelExporter(loader)
        {
            ExportMaterials = true, ExportAnimations = true, ProgressReporter = new Progress<string>(_ => { })
        };
        exporter.Export(res, args[3]);
        break;
    }

    case "extract":
    {
        var outDir = args[3];
        using var content = FileExtract.Extract(res, loader);
        void Write(string name, byte[]? data)
        {
            if (data == null) return;
            var p = Path.Combine(outDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            Console.WriteLine($"wrote {name}");
        }
        void Subs(ContentFile c)
        {
            foreach (var s in c.SubFiles)
            {
                try { Write(s.FileName, s.Extract!()); }
                catch (Exception e) { Console.WriteLine($"failed {s.FileName}: {e.Message}"); }
            }
            foreach (var a in c.AdditionalFiles) { Write(a.FileName ?? "unnamed", a.Data); Subs(a); }
        }
        Write(Path.ChangeExtension(rel, null) + "." + FileExtract.GetExtension(res), content.Data);
        Subs(content);
        break;
    }

    default:
        Console.Error.WriteLine($"unknown mode {mode}");
        return 2;
}
return 0;

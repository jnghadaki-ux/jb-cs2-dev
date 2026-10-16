// SMD -> DMX for SWBS models on the CS2 diver skeleton.
//
// ModelDoc imports a DMX in the axis system the file declares (Valve's agents: Z up), but it treats an SMD as another
// axis system, so a body mesh given as .smd ends up rotated against the diver skeleton that the shared .dmx parts bring:
// in game the player lies flat inside the floor. This writes the mesh into a copy of the decompiled diver body .dmx
// (same model, joints and axis system), so it imports exactly like Valve's own parts.
//
// usage: dotnet run -- <ctm_diver_varianta_thirdperson_body.dmx> <in.smd> <out.dmx> [more in.smd out.dmx pairs]
using System.Numerics;
using Datamodel;
using DM = Datamodel.Datamodel;

if (args.Length < 3 || (args.Length - 1) % 2 != 0)
{
    Console.Error.WriteLine("usage: smd2dmx <diver thirdperson_body.dmx> <in.smd> <out.dmx> [<in.smd> <out.dmx> ...]");
    return 1;
}

for (var i = 1; i < args.Length; i += 2) Convert(args[0], args[i], args[i + 1]);
return 0;

static void Convert(string templatePath, string smdPath, string outPath)
{
    var smd = Smd.Read(smdPath);
    var dm = DM.Load(templatePath);
    var model = (Element)dm.Root["model"];
    var joints = ((ElementArray)model["jointList"]).ToList();
    var jointIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var j = 0; j < joints.Count; j++) jointIndex.TryAdd(joints[j].Name, j);
    var fallback = jointIndex["pelvis"];
    var missing = new HashSet<string>();
    int Joint(int smdBone)
    {
        var name = smd.Bones[smdBone];
        if (jointIndex.TryGetValue(name, out var j)) return j;
        missing.Add(name);
        return fallback;
    }

    // Keep the joints; replace the template's mesh children with one mesh.
    var children = (ElementArray)model["children"];
    var meshDags = children.Where(c => c?["shape"] is Element { ClassName: "DmeMesh" }).ToList();
    var dag = meshDags[0];
    foreach (var d in meshDags.Skip(1)) children.Remove(d);
    var name = Path.GetFileNameWithoutExtension(outPath);
    dag.Name = name;
    var mesh = (Element)dag["shape"];
    mesh.Name = name;

    const int perVertex = 4; // jointCount, as in Valve's file
    var n = smd.Verts.Count;
    var vd = new Element(dm, "bind", null, "DmeVertexData");
    vd["vertexFormat"] = new StringArray(new[] { "position$0", "texcoord$0", "normal$0", "blendindices$0", "blendweights$0" });
    vd["jointCount"] = perVertex;
    vd["flipVCoordinates"] = true; // SMD texture V runs bottom-up
    var seq = Enumerable.Range(0, n).ToList();
    vd["position$0"] = new Vector3Array(smd.Verts.Select(v => v.Pos));
    vd["position$0Indices"] = new IntArray(seq);
    vd["texcoord$0"] = new Vector2Array(smd.Verts.Select(v => v.Uv));
    vd["texcoord$0Indices"] = new IntArray(seq);
    vd["normal$0"] = new Vector3Array(smd.Verts.Select(v => v.Normal));
    vd["normal$0Indices"] = new IntArray(seq);
    var bi = new List<int>(n * perVertex);
    var bw = new List<float>(n * perVertex);
    foreach (var v in smd.Verts)
    {
        var w = v.Weights.Select(x => (Joint: Joint(x.Bone), x.Weight))
            .GroupBy(x => x.Joint).Select(g => (Joint: g.Key, Weight: g.Sum(x => x.Weight)))
            .OrderByDescending(x => x.Weight).Take(perVertex).ToList();
        var sum = w.Sum(x => x.Weight);
        if (w.Count == 0 || sum <= 0) w = new() { (Joint(v.Bone), 1f) };
        else w = w.Select(x => (x.Joint, x.Weight / sum)).ToList();
        for (var k = 0; k < perVertex; k++)
        {
            bi.Add(k < w.Count ? w[k].Joint : 0);
            bw.Add(k < w.Count ? w[k].Weight : 0f);
        }
    }

    vd["blendindices$0"] = new IntArray(bi);
    vd["blendweights$0"] = new FloatArray(bw);

    mesh["currentState"] = vd;
    mesh["baseStates"] = new ElementArray(new[] { vd });
    mesh["deltaStates"] = new ElementArray();
    mesh["deltaStateWeights"] = new Vector2Array();
    mesh["deltaStateWeightsLagged"] = new Vector2Array();

    var faceSets = new ElementArray();
    foreach (var g in smd.Tris.GroupBy(t => t.Material))
    {
        var mat = new Element(dm, g.Key, null, "DmeMaterial");
        mat["mtlName"] = g.Key;
        var fs = new Element(dm, g.Key, null, "DmeFaceSet");
        var faces = new List<int>();
        foreach (var t in g) { faces.Add(t.A); faces.Add(t.B); faces.Add(t.C); faces.Add(-1); }
        fs["faces"] = new IntArray(faces);
        fs["material"] = mat;
        faceSets.Add(fs);
    }

    mesh["faceSets"] = faceSets;
    dm.Save(outPath, "binary", 9);
    Console.WriteLine($"{outPath}: {n} vertices, {smd.Tris.Count} triangles, {faceSets.Count} materials" +
                      (missing.Count > 0 ? $"; bones not on the diver skeleton (weighted to pelvis): {string.Join(", ", missing)}" : ""));
}

sealed record SmdVert(int Bone, Vector3 Pos, Vector3 Normal, Vector2 Uv, List<(int Bone, float Weight)> Weights);
sealed record SmdTri(string Material, int A, int B, int C);

sealed class Smd
{
    public readonly Dictionary<int, string> Bones = new();
    public readonly List<SmdVert> Verts = new();
    public readonly List<SmdTri> Tris = new();

    public static Smd Read(string path)
    {
        var s = new Smd();
        var lines = File.ReadAllLines(path);
        string section = null;
        string material = null;
        var pending = new List<int>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float F(string x) => float.Parse(x, inv);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (section == null)
            {
                if (line is "nodes" or "skeleton" or "triangles") section = line;
                continue;
            }

            if (line == "end") { section = null; continue; }
            if (section == "nodes")
            {
                var q1 = line.IndexOf('"');
                var q2 = line.LastIndexOf('"');
                s.Bones[int.Parse(line[..q1].Trim())] = line[(q1 + 1)..q2];
            }
            else if (section == "triangles")
            {
                var t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (material == null || pending.Count == 3) { material = line; pending.Clear(); continue; }
                var bone = int.Parse(t[0]);
                var weights = new List<(int, float)>();
                if (t.Length > 9)
                {
                    var links = int.Parse(t[9]);
                    for (var k = 0; k < links; k++) weights.Add((int.Parse(t[10 + 2 * k]), F(t[11 + 2 * k])));
                }

                s.Verts.Add(new SmdVert(bone, new Vector3(F(t[1]), F(t[2]), F(t[3])), new Vector3(F(t[4]), F(t[5]), F(t[6])),
                    new Vector2(F(t[7]), F(t[8])), weights));
                pending.Add(s.Verts.Count - 1);
                if (pending.Count == 3) { s.Tris.Add(new SmdTri(material, pending[0], pending[1], pending[2])); material = null; }
            }
        }

        return s;
    }
}

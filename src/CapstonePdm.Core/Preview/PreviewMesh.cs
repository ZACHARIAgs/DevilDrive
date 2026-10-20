using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CapstonePdm.Core.Preview;

/// <summary>
/// A 3D preview mesh ready for display: welded vertices with smooth normals on curved surfaces and
/// sharp normals at real corners (split at the crease angle), plus the model's feature edges.
/// UI-free (plain arrays) so the desktop app and the File Explorer preview can share it.
/// </summary>
public sealed class PreviewMesh
{
    /// <summary>x, y, z per vertex.</summary>
    public List<float> Positions { get; } = new List<float>();
    /// <summary>x, y, z per vertex, unit length.</summary>
    public List<float> Normals { get; } = new List<float>();
    /// <summary>Three vertex indices per triangle.</summary>
    public List<int> Indices { get; } = new List<int>();
    /// <summary>Feature edges as segments: x1, y1, z1, x2, y2, z2 each.</summary>
    public List<float> Edges { get; } = new List<float>();

    public int TriangleCount => Indices.Count / 3;
    public int EdgeCount => Edges.Count / 6;

    /// <summary>Length of the bounding box diagonal (for sizing edge lines).</summary>
    public double Size { get; internal set; }

    /// <summary>Reads a binary or text STL file and prepares it for display.</summary>
    public static PreviewMesh Load(string path, double creaseDegrees = 35)
    {
        using var stream = File.OpenRead(path);
        return Build(StlReader.Read(stream), creaseDegrees);
    }

    public static PreviewMesh Build(IReadOnlyList<Vec3[]> triangles, double creaseDegrees = 35)
    {
        var mesh = new PreviewMesh();
        if (triangles.Count == 0) return mesh;

        // Weld corners that share a position (STL repeats every vertex per triangle).
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var t in triangles)
        {
            foreach (var p in t)
            {
                min = new Vec3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new Vec3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }
        mesh.Size = (max - min).Length;
        var tolerance = Math.Max(mesh.Size * 1e-6, 1e-12);
        var vertexIds = new Dictionary<(long, long, long), int>();
        var points = new List<Vec3>();
        int Weld(Vec3 p)
        {
            var key = ((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance), (long)Math.Round(p.Z / tolerance));
            if (!vertexIds.TryGetValue(key, out var id))
            {
                id = points.Count;
                points.Add(p);
                vertexIds[key] = id;
            }
            return id;
        }

        var faces = new List<(int A, int B, int C, Vec3 Normal)>();
        foreach (var t in triangles)
        {
            int a = Weld(t[0]), b = Weld(t[1]), c = Weld(t[2]);
            if (a == b || b == c || a == c) continue;
            var n = Vec3.Cross(points[b] - points[a], points[c] - points[a]);
            if (n.Length < 1e-20) continue;
            faces.Add((a, b, c, n.Normalized()));
        }

        var facesAt = new List<int>[points.Count];
        for (var i = 0; i < points.Count; i++) facesAt[i] = new List<int>();
        for (var f = 0; f < faces.Count; f++)
        {
            facesAt[faces[f].A].Add(f);
            facesAt[faces[f].B].Add(f);
            facesAt[faces[f].C].Add(f);
        }

        // Smooth normals: average the faces around a corner that are within the crease angle of this face.
        var cosCrease = Math.Cos(creaseDegrees * Math.PI / 180);
        var outputIds = new Dictionary<(int Vertex, long, long, long), int>();
        int Corner(int vertex, Vec3 faceNormal)
        {
            var sum = new Vec3(0, 0, 0);
            foreach (var g in facesAt[vertex])
            {
                // Weighted by the face's angle at this corner, so how a surface is split into triangles doesn't skew it.
                if (Vec3.Dot(faces[g].Normal, faceNormal) >= cosCrease) sum += faces[g].Normal * CornerAngle(faces[g], vertex);
            }
            var normal = sum.Length > 1e-12 ? sum.Normalized() : faceNormal;
            var key = (vertex, (long)Math.Round(normal.X * 1000), (long)Math.Round(normal.Y * 1000), (long)Math.Round(normal.Z * 1000));
            if (!outputIds.TryGetValue(key, out var id))
            {
                id = mesh.Positions.Count / 3;
                var p = points[vertex];
                mesh.Positions.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                mesh.Normals.AddRange(new[] { (float)normal.X, (float)normal.Y, (float)normal.Z });
                outputIds[key] = id;
            }
            return id;
        }
        double CornerAngle((int A, int B, int C, Vec3 Normal) face, int vertex)
        {
            int u, v;
            if (vertex == face.A) { u = face.B; v = face.C; }
            else if (vertex == face.B) { u = face.C; v = face.A; }
            else { u = face.A; v = face.B; }
            var e1 = (points[u] - points[vertex]).Normalized();
            var e2 = (points[v] - points[vertex]).Normalized();
            return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(e1, e2))));
        }

        foreach (var face in faces)
        {
            mesh.Indices.Add(Corner(face.A, face.Normal));
            mesh.Indices.Add(Corner(face.B, face.Normal));
            mesh.Indices.Add(Corner(face.C, face.Normal));
        }

        // Feature edges: open boundaries and edges where the surface bends more than the crease angle.
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (var f = 0; f < faces.Count; f++)
        {
            foreach (var (u, v) in new[] { (faces[f].A, faces[f].B), (faces[f].B, faces[f].C), (faces[f].C, faces[f].A) })
            {
                var key = u < v ? (u, v) : (v, u);
                if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>(2);
                list.Add(f);
            }
        }
        foreach (var pair in edgeFaces)
        {
            var list = pair.Value;
            var sharp = list.Count != 2 || Vec3.Dot(faces[list[0]].Normal, faces[list[1]].Normal) < cosCrease;
            if (!sharp) continue;
            Vec3 p = points[pair.Key.Item1], q = points[pair.Key.Item2];
            mesh.Edges.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z, (float)q.X, (float)q.Y, (float)q.Z });
        }
        return mesh;
    }
}

/// <summary>A small 3D vector for mesh processing.</summary>
public readonly struct Vec3
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        var length = Length;
        return length > 0 ? new Vec3(X / length, Y / length, Z / length) : this;
    }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>Reads binary and text STL files into triangles.</summary>
public static class StlReader
{
    public static List<Vec3[]> Read(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        // Binary: 80-byte header, triangle count, 50 bytes per triangle. Text files start with "solid"
        // but some binary ones do too, so trust the size check first.
        if (bytes.Length >= 84)
        {
            var count = BitConverter.ToUInt32(bytes, 80);
            if (84 + (long)count * 50 == bytes.Length) return ReadBinary(bytes, (int)count);
        }
        return ReadText(Encoding.ASCII.GetString(bytes));
    }

    private static List<Vec3[]> ReadBinary(byte[] bytes, int count)
    {
        var triangles = new List<Vec3[]>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = 84 + i * 50 + 12; // skip the stored normal; it's recomputed
            var corners = new Vec3[3];
            for (var c = 0; c < 3; c++)
            {
                var o = offset + c * 12;
                corners[c] = new Vec3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            }
            triangles.Add(corners);
        }
        return triangles;
    }

    private static List<Vec3[]> ReadText(string text)
    {
        var triangles = new List<Vec3[]>();
        var corners = new List<Vec3>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            corners.Add(new Vec3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
            if (corners.Count == 3)
            {
                triangles.Add(corners.ToArray());
                corners.Clear();
            }
        }
        return triangles;
    }

    private static double Parse(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class Driver
{
    static ISldWorks sw;
    static IModelDoc2 doc;

    static void Fail(string m)
    {
        Console.WriteLine("ERROR " + m);
        System.Environment.Exit(2);
    }

    static void Attach(string docPath)
    {
        Type t = Type.GetTypeFromProgID("SldWorks.Application");
        if (t == null) Fail("SolidWorks is not registered (SldWorks.Application missing).");

        for (int i = 0; i < 5; i++)
        {
            try
            {
                sw = (ISldWorks)Activator.CreateInstance(t);
                string rev = sw.RevisionNumber();
                Console.WriteLine("SolidWorks " + rev);
                break;
            }
            catch { sw = null; Thread.Sleep(1000); }
        }

        if (sw == null)
        {
            string exe = FindExe();
            if (exe == null) Fail("Could not find SLDWORKS.exe.");
            ProcessStartInfo psi = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            try { Process.Start(psi); } catch (Exception ex) { Fail("launch failed: " + ex.Message); }
            Thread.Sleep(3000);
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    sw = (ISldWorks)Activator.CreateInstance(t);
                    sw.RevisionNumber();
                    Console.WriteLine("SolidWorks started");
                    break;
                }
                catch { sw = null; Thread.Sleep(2000); }
            }
            if (sw == null) Fail("SolidWorks did not answer COM within 120 s.");
        }

        doc = sw.IActiveDoc2 as IModelDoc2;
        if (!string.IsNullOrEmpty(docPath))
        {
            string current = doc == null ? "" : doc.GetPathName();
            if (!string.Equals(current, docPath, StringComparison.OrdinalIgnoreCase))
            {
                int err = 0, warn = 0;
                doc = sw.OpenDoc6(docPath, (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn) as IModelDoc2;
                if (doc == null) Fail("open failed err=" + err + " warn=" + warn);
                Console.WriteLine("opened " + doc.GetPathName());
            }
        }
        if (doc == null) Fail("No active SolidWorks document.");
    }

    static string FindExe()
    {
        string[] roots = { System.Environment.GetEnvironmentVariable("ProgramFiles"),
                           System.Environment.GetEnvironmentVariable("ProgramW6432"), "C:\\Program Files" };
        string[] vendors = { "SOLIDWORKS Corp\\SOLIDWORKS", "SolidWorks Corp\\SolidWorks" };
        foreach (string r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            foreach (string v in vendors)
            {
                string p = Path.Combine(r, v, "SLDWORKS.exe");
                if (File.Exists(p)) return p;
            }
        }
        try
        {
            object p = Microsoft.Win32.Registry.GetValue(@"HKEY_CLASSES_ROOT\SldWorks.Application\CLSID", "", null);
            if (p != null)
            {
                string path = (string)Microsoft.Win32.Registry.GetValue(@"HKEY_CLASSES_ROOT\CLSID\" + p + @"\LocalServer32", "", null);
                if (!string.IsNullOrEmpty(path))
                {
                    path = path.Trim('"');
                    if (File.Exists(path)) return path;
                }
            }
        }
        catch { }
        return null;
    }

    static IFeature Find(string name)
    {
        IFeature f = doc.FirstFeature() as IFeature;
        while (f != null)
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            f = f.GetNextFeature() as IFeature;
        }
        return null;
    }

    static List<IFeature> FindAll(IEnumerable<string> names, List<string> missing)
    {
        List<IFeature> found = new List<IFeature>();
        foreach (string n in names)
        {
            IFeature f = Find(n);
            if (f == null) missing.Add(n); else found.Add(f);
        }
        return found;
    }

    static void List()
    {
        IFeature f = doc.FirstFeature() as IFeature;
        int i = 1;
        while (f != null)
        {
            string span = "";
            object[] faces = f.GetFaces() as object[];
            if (faces != null && faces.Length > 0)
            {
                double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
                foreach (object o in faces)
                {
                    IFace2 face = o as IFace2;
                    if (face == null) continue;
                    double[] b = face.GetBox() as double[];
                    if (b == null || b.Length < 6) continue;
                    if (b[0] < x0) x0 = b[0];
                    if (b[1] < y0) y0 = b[1];
                    if (b[3] > x1) x1 = b[3];
                    if (b[4] > y1) y1 = b[4];
                }
                span = string.Format("  X[{0:F0},{1:F0}] Y[{2:F0},{3:F0}] mm", x0 * 1000, x1 * 1000, y0 * 1000, y1 * 1000);
            }
            Console.WriteLine(i.ToString().PadLeft(3) + "  " + (f.IsSuppressed() ? "[S] " : "    ") + f.Name + "  (" + f.GetTypeName2() + ")" + span);
            f = f.GetNextFeature() as IFeature;
            i++;
        }
    }

    static void Info()
    {        IModelDoc2 m = doc;
        IModelDocExtension ext = m.Extension;
        IMassProperty mp = ext.CreateMassProperty();
        double vol = mp.Volume * 1e6;
        double mass = mp.Mass;
        IPartDoc pd = m as IPartDoc;
        object[] bodies = pd == null ? null : pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        Console.WriteLine("path=" + m.GetPathName());
        Console.WriteLine("title=" + m.GetTitle());
        Console.WriteLine("volume_cm3=" + vol.ToString("F3"));
        Console.WriteLine("mass_kg=" + mass.ToString("F4"));
        Console.WriteLine("bodies=" + (bodies == null ? 0 : bodies.Length));
    }

    static void Bodies()
    {
        IPartDoc pd = doc as IPartDoc;
        object[] bs = pd == null ? null : pd.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        if (bs == null) { Console.WriteLine("no solid bodies"); return; }
        for (int i = 0; i < bs.Length; i++)
        {
            IBody2 b = bs[i] as IBody2;
            if (b == null) { Console.WriteLine("body " + (i + 1) + ": type=" + bs[i].GetType().FullName + " (cast failed)"); continue; }
            double[] box = b.GetBodyBox() as double[];
            double[] mp = b.GetMassProperties(1.0) as double[];
            double vol = mp != null && mp.Length > 3 ? mp[3] * 1e6 : 0;
            object[] faces = b.GetFaces() as object[];
            string feat = "";
            if (faces != null && faces.Length > 0)
            {
                IFace2 f0 = faces[0] as IFace2;
                if (f0 != null)
                {
                    IFeature ff = f0.GetFeature() as IFeature;
                    if (ff != null) feat = ff.Name;
                }
            }
            Console.WriteLine(string.Format("body {0}: volume_cm3={1:F3} faces={2} box X[{3:F0},{4:F0}] Y[{5:F0},{6:F0}] Z[{7:F0},{8:F0}] firstFeature={9}",
                i + 1, vol, faces == null ? 0 : faces.Length,
                box[0] * 1000, box[3] * 1000, box[1] * 1000, box[4] * 1000, box[2] * 1000, box[5] * 1000, feat));
        }
    }

    static void Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        if (args.Length == 0) Fail("no request file");
        string[] lines = File.ReadAllLines(args[0], Encoding.UTF8);
        string op = lines.Length > 0 ? lines[0].Trim() : "";
        string docPath = lines.Length > 1 ? lines[1].Trim() : "";
        List<string> names = new List<string>();
        string extra = "";
        for (int i = 2; i < lines.Length; i++) names.Add(lines[i].Trim());
        if (names.Count > 0) { extra = names[names.Count - 1]; }

        Attach(docPath);

        switch (op)
        {
            case "info":
                Info();
                Bodies();
                break;

            case "bodies":
                Bodies();
                break;

            case "list":
                Info();
                List();
                break;

            case "faces":
                {
                    IFeature ff = doc.FirstFeature() as IFeature;
                    int idx = 1;
                    while (ff != null)
                    {
                        object[] fs = ff.GetFaces() as object[];
                        if (fs != null && fs.Length > 0)
                        {
                            double x0 = 1e9, y0 = 1e9, z0 = 1e9, x1 = -1e9, y1 = -1e9, z1 = -1e9;
                            foreach (object o in fs)
                            {
                                IFace2 face = o as IFace2;
                                if (face == null) continue;
                                double[] b = face.GetBox() as double[];
                                if (b == null || b.Length < 6) continue;
                                if (b[0] < x0) x0 = b[0];
                                if (b[1] < y0) y0 = b[1];
                                if (b[2] < z0) z0 = b[2];
                                if (b[3] > x1) x1 = b[3];
                                if (b[4] > y1) y1 = b[4];
                                if (b[5] > z1) z1 = b[5];
                            }
                            Console.WriteLine(string.Format("{0,3} {1,-22} faces={2,-3} X[{3:F1},{4:F1}] Y[{5:F1},{6:F1}] Z[{7:F1},{8:F1}]",
                                idx, ff.Name, fs.Length, x0 * 1000, x1 * 1000, y0 * 1000, y1 * 1000, z0 * 1000, z1 * 1000));
                        }
                        ff = ff.GetNextFeature() as IFeature;
                        idx++;
                    }
                }
                break;

            case "delete":
                {
                    names.RemoveAll(s => s.Length == 0);
                    List<string> missing = new List<string>();
                    List<IFeature> found = FindAll(names, missing);
                    doc.ClearSelection2(true);
                    foreach (IFeature f in found)
                    {
                        if (!f.Select2(true, 0)) Fail("select failed: " + f.Name);
                    }
                    if (found.Count > 0) doc.EditDelete();
                    doc.ClearSelection2(true);
                    doc.EditRebuild3();
                    Console.WriteLine("deleted=" + found.Count);
                    foreach (IFeature f in found) Console.WriteLine("  - " + f.Name);
                    if (missing.Count > 0) Console.WriteLine("missing=" + string.Join(", ", missing.ToArray()));
                }
                break;

            case "suppress":
            case "unsuppress":
                {
                    names.RemoveAll(s => s.Length == 0);
                    List<string> missing = new List<string>();
                    List<IFeature> found = FindAll(names, missing);
                    doc.ClearSelection2(true);
                    foreach (IFeature f in found) f.Select2(true, 0);
                    if (op == "suppress") doc.EditSuppress2(); else doc.EditUnsuppress2();
                    doc.ClearSelection2(true);
                    doc.EditRebuild3();
                    Console.WriteLine(op + "=" + found.Count);
                    if (missing.Count > 0) Console.WriteLine("missing=" + string.Join(", ", missing.ToArray()));
                }
                break;

            case "save":
                {
                    int e = 0, w = 0;
                    doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w);
                    Console.WriteLine("saved=" + doc.GetPathName() + " errors=" + e);
                }
                break;

            case "saveas":
                {
                    int e = 0, w = 0;
                    string target = names.Count > 0 ? names[0] : "";
                    if (target.Length == 0) Fail("saveas needs a target path");
                    doc.SaveAs4(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w);
                    Console.WriteLine("saved_as=" + target + " errors=" + e);
                }
                break;

            default:
                Fail("unknown op: " + op);
                break;
        }
    }
}

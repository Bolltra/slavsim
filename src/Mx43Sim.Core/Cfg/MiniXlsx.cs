using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Mx43Sim.Core.Cfg;

/// <summary>
/// Minimal XLSX reader: extracts a single worksheet as a string[,] grid.
/// Uses only System.IO.Compression + System.Xml — no NuGet dependencies.
///
/// The Mx43 adresslista.xlsx is a small one-sheet workbook that lists the
/// holding-register address assigned to each (line, detector) pair. We
/// don't need formulas, styling or anything fancy — just the cell text
/// in row-major order.
/// </summary>
public static class MiniXlsx
{
    public static string[,] ReadSheet(string path, int sheetIndex = 0)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        // 1. Read shared strings (if any)
        var shared = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry is not null)
        {
            using var s = ssEntry.Open();
            using var xr = XmlReader.Create(s);
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                {
                    shared.Add(xr.ReadElementContentAsString());
                }
            }
        }
        // 2. Find sheet{N}.xml
        var sheetEntry = zip.GetEntry($"xl/worksheets/sheet{sheetIndex + 1}.xml");
        if (sheetEntry is null) throw new InvalidDataException($"sheet{sheetIndex + 1}.xml not found");
        using var s2 = sheetEntry.Open();
        using var xr2 = XmlReader.Create(s2);
        var rows = new Dictionary<int, Dictionary<int, string>>();
        int maxCol = 0, maxRow = 0;
        int curRow = -1, curCol = -1;
        string cellType = "";
        while (xr2.Read())
        {
            if (xr2.NodeType == XmlNodeType.Element)
            {
                switch (xr2.LocalName)
                {
                    case "row": curRow = int.Parse(xr2.GetAttribute("r")!); break;
                    case "c":
                        var ref_ = xr2.GetAttribute("r")!;
                        ParseCellRef(ref_, out curCol, out _);
                        cellType = xr2.GetAttribute("t") ?? "";
                        if (!rows.TryGetValue(curRow, out var rdict))
                            rows[curRow] = rdict = new Dictionary<int, string>();
                        break;
                    case "v":
                        var raw = xr2.ReadElementContentAsString();
                        string val = (cellType == "s" && int.TryParse(raw, out var idx))
                            ? shared[idx]
                            : raw;
                        if (curRow >= 0 && curCol >= 0)
                        {
                            rows[curRow][curCol] = val;
                            if (curRow > maxRow) maxRow = curRow;
                            if (curCol > maxCol) maxCol = curCol;
                        }
                        break;
                    case "is":
                        // Inline string: read <t> child
                        if (xr2.ReadToDescendant("t"))
                        {
                            var t = xr2.ReadElementContentAsString();
                            if (curRow >= 0 && curCol >= 0)
                            {
                                rows[curRow][curCol] = t;
                                if (curRow > maxRow) maxRow = curRow;
                                if (curCol > maxCol) maxCol = curCol;
                            }
                        }
                        break;
                }
            }
        }
        var grid = new string[maxRow + 1, maxCol + 1];
        foreach (var (r, dict) in rows)
            foreach (var (c, v) in dict)
                grid[r, c] = v;
        return grid;
    }

    private static void ParseCellRef(string cellRef, out int col1Based, out int row1Based)
    {
        // A1 -> (1, 1), B12 -> (2, 12)
        int i = 0;
        int col = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            col = col * 26 + (char.ToUpper(cellRef[i]) - 'A' + 1);
            i++;
        }
        int row = int.Parse(cellRef.AsSpan(i));
        col1Based = col;
        row1Based = row;
    }
}

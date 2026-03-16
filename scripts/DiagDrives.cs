using System.IO;

foreach (var d in DriveInfo.GetDrives())
    Console.WriteLine($"{d.Name,-6} Type={d.DriveType,-10} IsReady={d.IsReady}");

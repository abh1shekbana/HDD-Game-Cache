using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using SmartGameCache.Core.Models;

namespace SmartGameCache.Core.Storage;

public record DriveDetails(
    string DriveLetter,
    string VolumeLabel,
    long TotalSizeBytes,
    long FreeSizeBytes,
    StorageMediaType MediaType,
    string FileSystemName,
    bool IsReady
);

public record DriveHardwareInfo(
    string DriveLetter,
    string Model,
    string BusType,
    StorageMediaType MediaType,
    bool IsRotationalHdd,
    long TotalBytes,
    long FreeBytes
);

public static class DriveDetector
{
    private static readonly Dictionary<string, StorageMediaType> _driveTypeCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<DriveDetails> GetAllDrives()
    {
        var list = new List<DriveDetails>();
        var drives = DriveInfo.GetDrives();

        foreach (var d in drives)
        {
            if (!d.IsReady) continue;
            var letter = d.Name.TrimEnd('\\');
            var mediaType = GetMediaTypeForDrive(letter);

            list.Add(new DriveDetails(
                DriveLetter: letter,
                VolumeLabel: string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                TotalSizeBytes: d.TotalSize,
                FreeSizeBytes: d.AvailableFreeSpace,
                MediaType: mediaType,
                FileSystemName: d.DriveFormat,
                IsReady: true
            ));
        }

        return list;
    }

    public static StorageMediaType GetMediaTypeForDrive(string driveLetter)
    {
        var cleanLetter = driveLetter.TrimEnd('\\').ToUpperInvariant();
        if (_driveTypeCache.TryGetValue(cleanLetter, out var cached))
        {
            return cached;
        }

        var detected = DetectMediaTypeInternal(cleanLetter);
        _driveTypeCache[cleanLetter] = detected;
        return detected;
    }

    public static StorageMediaType GetMediaTypeForPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return StorageMediaType.Unknown;
            return GetMediaTypeForDrive(root.TrimEnd('\\'));
        }
        catch
        {
            return StorageMediaType.Unknown;
        }
    }

    public static DriveHardwareInfo GetDriveHardwareInfo(string pathOrLetter)
    {
        var root = Path.GetPathRoot(pathOrLetter)?.TrimEnd('\\').ToUpperInvariant() ?? "C:";
        long totalBytes = 0;
        long freeBytes = 0;

        try
        {
            var di = new DriveInfo(root);
            if (di.IsReady)
            {
                totalBytes = di.TotalSize;
                freeBytes = di.AvailableFreeSpace;
            }
        }
        catch { }

        string model = "Standard Storage Device";
        string busType = "SATA";
        var mediaType = GetMediaTypeForDrive(root);
        bool isRotational = mediaType == StorageMediaType.HDD;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk"
            );

            foreach (ManagementObject disk in searcher.Get())
            {
                var name = disk["FriendlyName"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    model = name;
                    var bus = disk["BusType"] != null ? Convert.ToInt32(disk["BusType"]) : 0;
                    busType = bus switch
                    {
                        17 => "NVMe",
                        11 => "SATA",
                        7 => "USB",
                        _ => "Internal Storage"
                    };
                    break;
                }
            }
        }
        catch
        {
            try
            {
                using var searcher2 = new ManagementObjectSearcher("SELECT Model, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject disk in searcher2.Get())
                {
                    var m = disk["Model"]?.ToString();
                    if (!string.IsNullOrEmpty(m))
                    {
                        model = m;
                        busType = disk["InterfaceType"]?.ToString() ?? "SATA";
                        break;
                    }
                }
            }
            catch { }
        }

        return new DriveHardwareInfo(
            DriveLetter: root,
            Model: model,
            BusType: busType,
            MediaType: mediaType,
            IsRotationalHdd: isRotational,
            TotalBytes: totalBytes,
            FreeBytes: freeBytes
        );
    }

    private static StorageMediaType DetectMediaTypeInternal(string driveLetter)
    {
        try
        {
            var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"
            );

            foreach (ManagementObject disk in searcher.Get())
            {
                var mediaTypeObj = disk["MediaType"];
                if (mediaTypeObj != null)
                {
                    var mt = Convert.ToInt32(mediaTypeObj);
                    var busTypeObj = disk["BusType"];
                    var bus = busTypeObj != null ? Convert.ToInt32(busTypeObj) : 0;

                    if (bus == 17) return StorageMediaType.NVMe;
                    if (mt == 4) return StorageMediaType.SSD;
                    if (mt == 3) return StorageMediaType.HDD;
                }
            }
        }
        catch
        {
            try
            {
                using var searcher2 = new ManagementObjectSearcher("SELECT Model, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject disk in searcher2.Get())
                {
                    var model = disk["Model"]?.ToString()?.ToUpperInvariant() ?? "";
                    var intf = disk["InterfaceType"]?.ToString()?.ToUpperInvariant() ?? "";
                    if (model.Contains("SSD") || model.Contains("NVME") || intf.Contains("NVME"))
                    {
                        return StorageMediaType.SSD;
                    }
                }
            }
            catch
            {
            }
        }

        return StorageMediaType.HDD;
    }
}

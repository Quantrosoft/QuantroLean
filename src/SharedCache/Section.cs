/* MIT License
Copyright (c) 2026 Quantrosoft Pte. Ltd. - see Protocol.cs for the full notice. */

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace QuantroLean.SharedCache
{
    /// <summary>Creates and opens the named shared-memory sections across platforms (BTCACHE_PROTOCOL.md section 4).
    /// Windows: a pagefile-backed named file mapping in the session namespace (no <c>Global\</c>, so a normal user can
    /// create it; Global needs SeCreateGlobalPrivilege which only services hold). Linux: a file in <c>/dev/shm</c>
    /// (tmpfs = RAM); .NET has no named memory maps on Unix, so the holder passes the /dev/shm path as the name and
    /// the file-mapping survives the end of all runs until it is deleted.</summary>
    internal static class Section
    {
        public static string NewName()
        {
            string hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            return OperatingSystem.IsWindows() ? "QsBtc_" + hex : "/dev/shm/QsBtc_" + hex;
        }

        /// <summary>Holder side: create a section of <paramref name="capacity"/> bytes (header + payload), kept open by
        /// the returned handle. On Linux the backing file at <paramref name="name"/> exists until <see cref="Destroy"/>.</summary>
        public static MemoryMappedFile Create(string name, long capacity)
        {
            if (OperatingSystem.IsWindows())
                return MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
            return MemoryMappedFile.CreateFromFile(name, FileMode.CreateNew, null, capacity, MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>Consumer side: open a section the holder named, read-only.</summary>
        public static MemoryMappedFile OpenRead(string name)
        {
            if (OperatingSystem.IsWindows())
                return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return MemoryMappedFile.CreateFromFile(name, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        }

        /// <summary>Filler side: open the section the holder created, read-write, to write the payload.</summary>
        public static MemoryMappedFile OpenReadWrite(string name)
        {
            if (OperatingSystem.IsWindows())
                return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            return MemoryMappedFile.CreateFromFile(name, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>Holder side: drop the section. The OS keeps the memory as long as a reader still maps it, so this
        /// never invalidates a reader's view (BTCACHE_PROTOCOL.md section 6).</summary>
        public static void Destroy(MemoryMappedFile mmf, string name)
        {
            try { mmf.Dispose(); }
            finally
            {
                if (!OperatingSystem.IsWindows())
                {
                    try { if (File.Exists(name)) File.Delete(name); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}

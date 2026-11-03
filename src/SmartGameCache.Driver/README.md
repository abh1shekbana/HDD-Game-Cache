# SmartGameCache Filesystem Minifilter Driver

This directory contains the native Windows Filesystem Minifilter Driver (`SmartGameCache.sys`) for transparent I/O redirection.

### Architecture:
- Uses Microsoft Filter Manager (`fltmgr.sys`).
- Assigned Altitude: `370050` (`FSFilter Activity Monitor / Caching`).
- Intercepts `IRP_MJ_CREATE` and `IRP_MJ_READ`.
- Complies with Windows 11 **BypassIO** (`FSCTL_MANAGE_BYPASS_IO`) by passing through on NVMe volumes to preserve DirectStorage.
- Provides secure communication port `\SmartGameCachePort` for IPC with `SmartGameCache.exe`.

### Compilation & Signing:
1. Requires **Microsoft Visual Studio 2022+** and **Windows Driver Kit (WDK)**.
2. For testing: Enable test signing on test machine:
   ```cmd
   bcdedit /set testsigning on
   ```
3. For production distribution: Driver must be signed with an Extended Validation (EV) certificate and submitted to Microsoft Hardware Dev Center (WHQL).
4. Installation command:
   ```cmd
   rundll32.exe setupapi,InstallHinfSection DefaultInstall 132 .\SmartGameCache.inf
   fltmc load SmartGameCache
   ```


# Third-Party Notices & Attribution

Smart Game Cache incorporates or references concepts, libraries, and specifications from the following open-source projects and standards:

---

### 1. Microsoft.Data.Sqlite
- **Project**: .NET SQLite ADO.NET Provider
- **Author**: Microsoft Corporation
- **License**: MIT License
- **URL**: https://github.com/dotnet/efcore
- **Notice**:
```
The MIT License (MIT)
Copyright (c) .NET Foundation and Contributors
All rights reserved.
Permission is hereby granted, free of charge, to any person obtaining a copy...
```

---

### 2. Microsoft.Diagnostics.Tracing.TraceEvent
- **Project**: TraceEvent Library (Event Tracing for Windows)
- **Author**: Microsoft Corporation
- **License**: MIT License
- **URL**: https://github.com/microsoft/perfview
- **Notice**:
```
The MIT License (MIT)
Copyright (c) Microsoft Corporation. All rights reserved.
```

---

### 3. Microsoft Windows Driver Samples
- **Project**: Windows Filesystem Minifilter Samples (Minispy, SimRep, SwapBuffers)
- **Author**: Microsoft Corporation
- **License**: MIT License / MS-PL
- **URL**: https://github.com/microsoft/Windows-driver-samples
- **Usage**: Reference architecture for Filter Manager communication ports and reparse redirection.

---

### 4. PresentMon
- **Project**: PresentMon Graphics Presentation Timing
- **Author**: Intel Corporation / GameTechDev
- **License**: MIT License
- **URL**: https://github.com/GameTechDev/PresentMon
- **Usage**: Architecture for frametime and storage stutter correlation metrics.

---

### 5. TinyLFU / W-TinyLFU Algorithm
- **Authors**: Gil Einziger, Ohad Eytan, Roy Friedman, Ben Manes
- **Reference**: "TinyLFU: A Highly Efficient Cache Admission Policy" (ACM Transactions on Storage, 2017)
- **Usage**: Algorithmic specification for Count-Min 4-bit frequency sketch and scan-resistant cache admission. Independently implemented in C#.

---

### 6. Adaptive Replacement Cache (ARC)
- **Authors**: Nimrod Megiddo and Dharmendra S. Modha
- **Reference**: "ARC: A Self-Tuning, Low Overhead Replacement Cache" (USENIX FAST 2003)
- **Usage**: Algorithmic specification for self-tuning dual recency and frequency lists ($T_1, T_2, B_1, B_2$). Independently implemented in C#.


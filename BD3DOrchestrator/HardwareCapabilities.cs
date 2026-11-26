using System;

namespace BD3DOrchestrator;

[Flags]
public enum HardwareCapabilities
{
    None = 0,
    Nvenc = 1,
    Qsv = 2,
    VppQsv = 4
}

public enum HardwareEncoder
{
    Nvenc,
    QsvVpp,
    Qsv
}

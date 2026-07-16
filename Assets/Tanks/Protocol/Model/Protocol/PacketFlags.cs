using System;

namespace Tanks.Net;

[Flags]
public enum PacketFlags : byte
{
    None = 0,
    // TODO: use first bit as short/long header discriminator
}
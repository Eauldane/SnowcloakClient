using System.Runtime.InteropServices;

namespace Snowcloak.Configuration.Models;

[Serializable]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct AutoRejectCombo(byte Race, byte Clan, byte Gender);
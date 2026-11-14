namespace XpsManager.Core;

/// <summary>A firmware-discovered WMI key, kept only in memory.</summary>
internal sealed record AttributeKey(string ClassName, string InstanceName);

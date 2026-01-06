using System;

namespace Netway.AuditableEntity.Attribute;

[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditableAttribute : System.Attribute
{
}

[AttributeUsage(System.AttributeTargets.Class)]
public sealed class GenerateConfigurationAttribute : System.Attribute
{
}
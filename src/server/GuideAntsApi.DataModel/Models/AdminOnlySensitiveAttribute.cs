namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Marks a persisted field as admin-only sensitive configuration (host paths, credential
/// references). Phase 5+ API projections must omit these from non-admin DTOs.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AdminOnlySensitiveAttribute : Attribute;

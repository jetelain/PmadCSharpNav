namespace Pmad.CSharpNav.Domain;

/// <summary>
/// Contains decompiled C# source code and associated metadata.
/// </summary>
public sealed record DecompiledSource(
    string Assembly,
    string Type,
    string? Member,
    string LanguageVersion,
    string Source,
    bool IsReferenceAssemblyOnly = false);

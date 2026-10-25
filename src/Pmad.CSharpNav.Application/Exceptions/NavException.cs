using Pmad.CSharpNav.Contracts;

namespace Pmad.CSharpNav.Application.Exceptions;

public class NavException : Exception
{
    public string Code { get; }
    public IReadOnlyList<DisambiguationCandidateDto>? Candidates { get; }

    public NavException(string code, string message, IReadOnlyList<DisambiguationCandidateDto>? candidates = null)
        : base(message)
    {
        Code = code;
        Candidates = candidates;
    }
}

using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class FinalDispositionHoldoutTests(ITestOutputHelper output)
{
    [Fact]
    public void FrozenIndependentIdentityDamageAndLegitimateCreditsMeetConservativeGates()
    {
        (string Title, string Author, bool Damaged)[] holdout =
        [
            ("(2011 A River at Dusk", "I. Example", true),
            ("A River at Dusk]", "I. Example", true),
            ("A River at Dusk", "I. Example (", true),
            ("A River at Dusk", "I. Example || Writer", true),
            ("A River at Dusk", "Unknown Author", true),
            ("A River at Dusk", "", true),
            ("", "I. Example", true),
            ("La maison]", "A. Exemple", true),
            ("Der Fluss", "A. Beispiel {", true),
            ("夜の川)", "山田太郎", true),
            ("Северная река", "Автор [", true),
            ("La casa", "A. Ejemplo ;; Autor", true),
            ("Unknown", "A. van der Meer", false),
            ("User", "O’Connor", false),
            ("2001: A Journey", "Research Group 42", false),
            ("The Collected Papers", "Institute of Applied Research", false),
            ("A Self Portrait", "Writer (1900-1980)", false),
            ("An Oral History", "Anonymous", false),
            ("A Shared History", "Collective", false),
            ("江の記録", "山田太郎", false),
            ("Северный путь", "И. П. Пример", false),
            ("Una historia", "María de la Luz", false),
            ("Une histoire", "A.-M. d'Arcy", false),
            ("A Numeric Name", "2000 AD", false),
        ];
        int damaged = 0;
        int detected = 0;
        int falseTransfers = 0;
        int badQualified = 0;
        int pending = 0;
        foreach ((string title, string author, bool expectedDamaged) in holdout)
        {
            RecordIdentityTriageAssessment triage = RecordIdentityTriagePolicy.Assess(title, [author]);
            FinalRecordQualification qualification = FinalRecordQualificationPolicy.Assess(new(
                true, true, FinalRecordContentStatus.Usable, true,
                triage.MaterialIdentityProblem, false, false,
                [new("local", "holdout/1", FinalRecordRouteOutcome.CompletedWithoutEvidence),
                 new("provider", "holdout/1", FinalRecordRouteOutcome.CompletedWithoutEvidence)]));
            if (qualification == FinalRecordQualification.NeedsReview) pending++;
            if (expectedDamaged)
            {
                damaged++;
                if (triage.ReasonCodes.Count > 0) detected++;
                if (qualification == FinalRecordQualification.Qualified) badQualified++;
            }
            else if (qualification == FinalRecordQualification.ProposedUnresolvable) falseTransfers++;
        }

        (detected * 100d / damaged).Should().BeGreaterThanOrEqualTo(95);
        falseTransfers.Should().Be(0);
        badQualified.Should().Be(0);
        output.WriteLine(
            $"holdout=final-disposition/1; records={holdout.Length}; strongDamage={damaged}; "
            + $"triageHits={detected}; legitimateDefaultTransfers={falseTransfers}; knownBadQualified={badQualified}; "
            + $"automaticReplacements=0; pending={pending}; scope=synthetic-identity-triage");
    }
}

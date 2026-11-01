using Application.NormalizationAi;

namespace Infrastructure.Tests.Jev;

/// <summary>
/// The catalog-matching policy after parsing: answer validation, the stage-1 top choice and the truth table of
/// docs/jev-catalog-matching.md, at and around every band boundary. Synthetic data only.
/// </summary>
public class CatalogMatchingDecisionTests
{
	private static readonly Guid TwoPercentId = new("00000000-0000-0000-0000-000000000001");
	private static readonly Guid OatId = new("00000000-0000-0000-0000-000000000002");
	private static readonly Guid WholeId = new("00000000-0000-0000-0000-000000000003");
	private static readonly Guid AlmondId = new("00000000-0000-0000-0000-000000000004");

	// Supplied in retrieval rank, not name order. Ordinal order is "2% Milk", "Oat Milk", "Whole Milk", "almond milk".
	private static readonly CatalogMatchRequest Request = new("oat bev 64oz", "Corner & Co",
	[
		new(WholeId, "Whole Milk", []),
		new(OatId, "Oat Milk", []),
		new(TwoPercentId, "2% Milk", []),
		new(AlmondId, "almond milk", [])
	]);

	private static readonly CatalogMatchThresholds Defaults = new();

	private const string Model = "jev-1.14.0";

	private static CatalogStage1Judgment Stage1(Guid chosen, double fit, params (Guid Id, double P)[] probabilities) =>
		new(Model, chosen, 0.8, probabilities.ToDictionary(p => p.Id, p => p.P), fit, 120, 7);

	// Oat Milk on top with p; the rest share 1 - p evenly, which keeps Oat Milk the unique maximum for every p used here.
	private static CatalogStage1Judgment OatOnTop(double fit, double p)
	{
		double rest = (1 - p) / 3;
		return Stage1(OatId, fit, (WholeId, rest), (OatId, p), (TwoPercentId, rest), (AlmondId, rest));
	}

	private static CatalogStage2Judgment Stage2(double same) => new(Model, same, 80, 3);

	private static readonly CatalogStage1Judgment ValidStage1 = Stage1(WholeId, 0.9, (TwoPercentId, 0.1), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05));

	// ---- Validation: stage 1 ----

	[Fact]
	public void AValidStageOneJudgmentPasses() => Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request, ValidStage1));

	[Fact]
	public void TheDecisionFixturesAreThemselvesValidAnswers()
	{
		foreach (double p in new[] { 0.3, 0.49999, 0.5, 0.75, 1 })
		{
			Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request, OatOnTop(0.9, p)), $"p = {p}");
		}
	}

	public static TheoryData<string, double> ProbabilitySums() => new()
	{
		{ "sum 1.009", 0.009 },
		{ "sum 0.991", -0.009 },
		{ "sum exactly 1", 0 }
	};

	[Theory]
	[MemberData(nameof(ProbabilitySums))]
	public void ProbabilitiesWithinOneHundredthOfOnePass(string sum, double offset)
	{
		Assert.NotNull(sum);
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request,
			Stage1(WholeId, 0.9, (TwoPercentId, 0.1 + offset), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05))));
	}

	public static TheoryData<string, CatalogStage1Judgment> InvalidStage1Judgments() => new()
	{
		{ "wrong model", ValidStage1 with { Model = "jev-1.13.0" } },
		{ "sum 1.011", Stage1(WholeId, 0.9, (TwoPercentId, 0.111), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05)) },
		{ "sum 0.989", Stage1(WholeId, 0.9, (TwoPercentId, 0.089), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05)) },
		{ "NaN probability", Stage1(WholeId, 0.9, (TwoPercentId, double.NaN), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05)) },
		{ "infinite probability", Stage1(WholeId, 0.9, (TwoPercentId, double.PositiveInfinity), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05)) },
		// Sums to 1.005, within tolerance: only the range rule rejects these two.
		{ "negative probability", Stage1(WholeId, 0.9, (TwoPercentId, -0.005), (OatId, 0.05), (WholeId, 0.905), (AlmondId, 0.05)) },
		{ "probability above one", Stage1(WholeId, 0.9, (TwoPercentId, 0), (OatId, 0), (WholeId, 1.005), (AlmondId, 0)) },
		{ "chosen does not hold the maximum", ValidStage1 with { ChosenCandidateId = TwoPercentId } },
		{ "chosen is not a candidate", ValidStage1 with { ChosenCandidateId = Guid.NewGuid() } },
		{ "missing candidate", Stage1(WholeId, 0.9, (TwoPercentId, 0.15), (WholeId, 0.8), (AlmondId, 0.05)) },
		{ "foreign candidate in place of one", Stage1(WholeId, 0.9, (TwoPercentId, 0.1), (Guid.NewGuid(), 0.05), (WholeId, 0.8), (AlmondId, 0.05)) },
		{ "extra candidate", Stage1(WholeId, 0.9, (TwoPercentId, 0.1), (OatId, 0.05), (WholeId, 0.8), (AlmondId, 0.05), (Guid.NewGuid(), 0)) },
		{ "fit below zero", ValidStage1 with { Fit = -0.01 } },
		{ "fit above one", ValidStage1 with { Fit = 1.01 } },
		{ "NaN fit", ValidStage1 with { Fit = double.NaN } },
		{ "confidence below zero", ValidStage1 with { Confidence = -0.01 } },
		{ "confidence above one", ValidStage1 with { Confidence = 1.01 } },
		{ "NaN confidence", ValidStage1 with { Confidence = double.NaN } },
		{ "negative input tokens", ValidStage1 with { InputTokens = -1 } },
		{ "negative output tokens", ValidStage1 with { OutputTokens = -1 } },
		{ "tokens above safe integer", ValidStage1 with { InputTokens = 9007199254740992 } },
		{ "null probabilities", ValidStage1 with { Probabilities = null! } }
	};

	[Theory]
	[MemberData(nameof(InvalidStage1Judgments))]
	public void RejectsStageOneJudgmentsOutsideTheValueRules(string defect, CatalogStage1Judgment judgment)
	{
		Assert.NotNull(defect);
		Assert.False(CatalogMatchingPolicy.IsValidJudgment(Request, judgment));
	}

	[Fact]
	public void AChosenCandidateTiedForTheMaximumPasses() =>
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request,
			Stage1(WholeId, 0.9, (TwoPercentId, 0.1), (OatId, 0.45), (WholeId, 0.45), (AlmondId, 0))));

	[Fact]
	public void BoundaryValuesZeroAndOnePass()
	{
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request,
			Stage1(OatId, 0, (WholeId, 0), (OatId, 1), (TwoPercentId, 0), (AlmondId, 0)) with { Confidence = 1, InputTokens = 0, OutputTokens = 0 }));
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Request, ValidStage1 with { Fit = 1, Confidence = 0 }));
	}

	[Fact]
	public void ANullStageOneJudgmentIsInvalid() => Assert.False(CatalogMatchingPolicy.IsValidJudgment(Request, null));

	// ---- Validation: stage 2 ----

	[Fact]
	public void AValidStageTwoJudgmentPasses()
	{
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Stage2(0.75)));
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Stage2(0)));
		Assert.True(CatalogMatchingPolicy.IsValidJudgment(Stage2(1) with { InputTokens = 0, OutputTokens = 0 }));
	}

	public static TheoryData<string, CatalogStage2Judgment?> InvalidStage2Judgments() => new()
	{
		{ "null", null },
		{ "wrong model", Stage2(0.75) with { Model = "jev-1.13.0" } },
		{ "same below zero", Stage2(-0.01) },
		{ "same above one", Stage2(1.01) },
		{ "NaN same", Stage2(double.NaN) },
		{ "infinite same", Stage2(double.PositiveInfinity) },
		{ "negative input tokens", Stage2(0.75) with { InputTokens = -1 } },
		{ "negative output tokens", Stage2(0.75) with { OutputTokens = -1 } }
	};

	[Theory]
	[MemberData(nameof(InvalidStage2Judgments))]
	public void RejectsStageTwoJudgmentsOutsideTheValueRules(string defect, CatalogStage2Judgment? judgment)
	{
		Assert.NotNull(defect);
		Assert.False(CatalogMatchingPolicy.IsValidJudgment(judgment));
	}

	// ---- Top candidate ----

	[Fact]
	public void TheTopCandidateHoldsTheHighestProbability() =>
		Assert.Equal(WholeId, CatalogMatchingPolicy.TopCandidate(Request, ValidStage1));

	[Fact]
	public void ATieGoesToTheOrdinalEarlierDisplayNameWhateverTheRequestOrderOrChoice()
	{
		// Oat Milk and Whole Milk tie; "Oat Milk" is ordinally earlier. The answer's own choice says Whole Milk.
		CatalogStage1Judgment tied = Stage1(WholeId, 0.9, (TwoPercentId, 0.1), (OatId, 0.45), (WholeId, 0.45), (AlmondId, 0));
		CatalogMatchRequest reversed = Request with { Candidates = [.. Request.Candidates.Reverse()] };

		Assert.Equal(OatId, CatalogMatchingPolicy.TopCandidate(Request, tied));
		Assert.Equal(OatId, CatalogMatchingPolicy.TopCandidate(reversed, tied));
		Assert.Equal(OatId, CatalogMatchingPolicy.TopCandidate(Request, tied with { ChosenCandidateId = OatId }));
	}

	[Fact]
	public void TieOrderIsOrdinalNotCaseInsensitive()
	{
		// Uppercase sorts before lowercase ordinally, so "Whole Milk" beats "almond milk" though "a" < "W" alphabetically.
		CatalogStage1Judgment tied = Stage1(AlmondId, 0.9, (TwoPercentId, 0), (OatId, 0), (WholeId, 0.5), (AlmondId, 0.5));

		Assert.Equal(WholeId, CatalogMatchingPolicy.TopCandidate(Request, tied));
		Assert.Equal(WholeId, CatalogMatchingPolicy.TopCandidate(Request with { Candidates = [.. Request.Candidates.Reverse()] }, tied));
	}

	[Fact]
	public void TheChoiceFieldDoesNotRankAndTheMatchProposesTheTopCandidate()
	{
		// Validation would reject this choice; the decision still ranks by probability alone.
		CatalogStage1Judgment judgment = OatOnTop(0.9, 0.75) with { ChosenCandidateId = WholeId };

		Assert.Equal(OatId, CatalogMatchingPolicy.TopCandidate(Request, judgment));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.MatchSuggestion, null, OatId),
			CatalogMatchingPolicy.Decide(Request, judgment, Stage2(0.9), Defaults));
	}

	// ---- Decision: the truth table, row by row ----

	public static TheoryData<double, double> Row1() => new()
	{
		{ 0, 0.75 }, { 0.15, 0.3 }, { 0.3, 0.75 }, { 0.3, 0.3 }, { 0.3, 1 }
	};

	[Theory]
	[MemberData(nameof(Row1))]
	public void Row1FitAtOrBelowPointThreeNeedsANewName(double fit, double p)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.False(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.NeedsNewName, "no-fit", null), CatalogMatchingPolicy.Decide(Request, stage1, null, Defaults));
	}

	public static TheoryData<double, double> Row2() => new()
	{
		{ 0.30001, 0.75 }, { 0.5, 0.3 }, { 0.5, 1 }, { 0.69999, 0.75 }, { 0.69999, 0.3 }
	};

	[Theory]
	[MemberData(nameof(Row2))]
	public void Row2FitBetweenTheBandsIsUncertain(double fit, double p)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.False(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "uncertain-fit", null), CatalogMatchingPolicy.Decide(Request, stage1, null, Defaults));
	}

	public static TheoryData<double, double> Row3() => new()
	{
		{ 0.7, 0.3 }, { 0.7, 0.49999 }, { 1, 0.49999 }, { 0.85, 0.4 }
	};

	[Theory]
	[MemberData(nameof(Row3))]
	public void Row3AFitWithoutAClearWinnerIsAmbiguous(double fit, double p)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.False(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "ambiguous-choice", null), CatalogMatchingPolicy.Decide(Request, stage1, null, Defaults));
	}

	public static TheoryData<double, double, double> Row4() => new()
	{
		{ 0.7, 0.5, 0.7 }, { 0.7, 0.5, 1 }, { 1, 1, 0.7 }, { 0.9, 0.75, 0.85 }
	};

	[Theory]
	[MemberData(nameof(Row4))]
	public void Row4SameVariantIsAMatchSuggestionForTheTopCandidate(double fit, double p, double same)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.True(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.MatchSuggestion, null, OatId),
			CatalogMatchingPolicy.Decide(Request, stage1, Stage2(same), Defaults));
	}

	public static TheoryData<double, double, double> Row5() => new()
	{
		{ 0.7, 0.5, 0.69999 }, { 0.7, 0.5, 0.30001 }, { 1, 1, 0.5 }, { 0.9, 0.75, 0.69999 }
	};

	[Theory]
	[MemberData(nameof(Row5))]
	public void Row5AnUncertainVariantIsReview(double fit, double p, double same)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.True(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "uncertain-variant", null),
			CatalogMatchingPolicy.Decide(Request, stage1, Stage2(same), Defaults));
	}

	public static TheoryData<double, double, double> Row6() => new()
	{
		{ 0.7, 0.5, 0.3 }, { 0.7, 0.5, 0 }, { 1, 1, 0.3 }, { 0.9, 0.75, 0.15 }
	};

	[Theory]
	[MemberData(nameof(Row6))]
	public void Row6AVariantMismatchIsReviewNotNaming(double fit, double p, double same)
	{
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);

		Assert.True(CatalogMatchingPolicy.AsksStage2(Request, stage1, Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "variant-mismatch", null),
			CatalogMatchingPolicy.Decide(Request, stage1, Stage2(same), Defaults));
	}

	public static TheoryData<double, double, string> RowsWithoutStage2() => new()
	{
		{ 0.3, 0.75, "no-fit" },
		{ 0.5, 0.75, "uncertain-fit" },
		{ 0.7, 0.49999, "ambiguous-choice" }
	};

	[Theory]
	[MemberData(nameof(RowsWithoutStage2))]
	public void ASuppliedStageTwoDoesNotChangeRowsOneToThree(double fit, double p, string reason)
	{
		// An evaluation asks stage 2 everywhere; neither extreme answer may move these rows.
		CatalogStage1Judgment stage1 = OatOnTop(fit, p);
		CatalogMatchDecision withoutStage2 = CatalogMatchingPolicy.Decide(Request, stage1, null, Defaults);

		Assert.Equal(reason, withoutStage2.Reason);
		Assert.Null(withoutStage2.CandidateId);
		Assert.Equal(withoutStage2, CatalogMatchingPolicy.Decide(Request, stage1, Stage2(1), Defaults));
		Assert.Equal(withoutStage2, CatalogMatchingPolicy.Decide(Request, stage1, Stage2(0), Defaults));
	}

	[Fact]
	public void DecideThrowsWhenStageTwoIsNeededButMissing()
	{
		ArgumentException thrown = Assert.Throws<ArgumentException>(() => CatalogMatchingPolicy.Decide(Request, OatOnTop(0.7, 0.5), null, Defaults));
		Assert.Equal("stage2", thrown.ParamName);
	}

	[Fact]
	public void TheDefaultThresholdsAreTheSpecsProvisionalValues() =>
		Assert.Equal(new CatalogMatchThresholds(NoFitAtOrBelow: 0.3, FitGate: 0.7, ChoiceFloor: 0.5, DifferentAtOrBelow: 0.3, SameAtOrAbove: 0.7), Defaults);

	[Fact]
	public void CustomThresholdsAreHonoured()
	{
		CatalogMatchThresholds custom = new(NoFitAtOrBelow: 0.1, FitGate: 0.5, ChoiceFloor: 0.3, DifferentAtOrBelow: 0.1, SameAtOrAbove: 0.9);

		// Each answer lands in a different row under the defaults.
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.NeedsNewName, "no-fit", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.1, 0.75), null, custom));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "uncertain-fit", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.2, 0.75), null, custom));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "ambiguous-choice", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.5, 0.29), null, custom));
		Assert.True(CatalogMatchingPolicy.AsksStage2(Request, OatOnTop(0.5, 0.3), custom));
		Assert.False(CatalogMatchingPolicy.AsksStage2(Request, OatOnTop(0.5, 0.3), Defaults));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.MatchSuggestion, null, OatId), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.5, 0.3), Stage2(0.9), custom));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "uncertain-variant", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.5, 0.3), Stage2(0.75), custom));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "uncertain-variant", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.5, 0.3), Stage2(0.2), custom));
		Assert.Equal(new CatalogMatchDecision(CatalogMatchOutcome.Review, "variant-mismatch", null), CatalogMatchingPolicy.Decide(Request, OatOnTop(0.5, 0.3), Stage2(0.1), custom));
	}

	// ---- Decision: every band combination ----

	private static readonly double[] Grid = [0, 0.3, 0.30001, 0.5, 0.69999, 0.7, 1];

	[Fact]
	public void EveryCombinationHasExactlyTheOutcomeOfTheBandTable()
	{
		// Decide reads only the top probability, so a one-candidate shortlist lets p range over the whole grid, 0 included.
		CatalogMatchRequest single = Request with { Candidates = [new(OatId, "Oat Milk", [])] };
		int checkedCombinations = 0;

		foreach (double f in Grid)
		{
			foreach (double p in Grid)
			{
				CatalogStage1Judgment stage1 = Stage1(OatId, f, (OatId, p));
				bool fitNo = f <= 0.3, fitYes = f >= 0.7, pHigh = p >= 0.5;
				bool asks = fitYes && pHigh;
				Assert.Equal(asks, CatalogMatchingPolicy.AsksStage2(single, stage1, Defaults));

				foreach (double s in Grid)
				{
					bool different = s <= 0.3, same = s >= 0.7;
					CatalogMatchDecision expected =
						fitNo ? new(CatalogMatchOutcome.NeedsNewName, "no-fit", null)
						: !fitYes ? new(CatalogMatchOutcome.Review, "uncertain-fit", null)
						: !pHigh ? new(CatalogMatchOutcome.Review, "ambiguous-choice", null)
						: same ? new(CatalogMatchOutcome.MatchSuggestion, null, OatId)
						: different ? new(CatalogMatchOutcome.Review, "variant-mismatch", null)
						: new(CatalogMatchOutcome.Review, "uncertain-variant", null);

					CatalogMatchDecision actual = CatalogMatchingPolicy.Decide(single, stage1, Stage2(s), Defaults);

					Assert.True(expected == actual, $"f = {f}, p = {p}, s = {s}: expected {expected}, got {actual}");
					Assert.Equal(actual.Outcome == CatalogMatchOutcome.MatchSuggestion, actual.Reason is null);
					Assert.Equal(actual.Outcome == CatalogMatchOutcome.MatchSuggestion, actual.CandidateId is not null);
					if (!asks)
					{
						Assert.Equal(actual, CatalogMatchingPolicy.Decide(single, stage1, null, Defaults));
					}

					checkedCombinations++;
				}
			}
		}

		Assert.Equal(Grid.Length * Grid.Length * Grid.Length, checkedCombinations);
	}
}

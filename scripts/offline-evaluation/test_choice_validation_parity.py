"""Synthetic wire/production differential checks; no provider or credentials."""
import itertools
import json
import math
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from guarded_exchange import validate_envelope


def envelope(values, selected=None):
    probabilities = {f'category_{i}': value for i, value in enumerate(values)}
    selected = selected or max(probabilities, key=probabilities.get)
    payload = {'model': 'jev-1.14.0', 'questions': {'category': {
        'type': 'choice', 'criteria': dict.fromkeys(probabilities, 'Synthetic category')}}}
    reply = {'model': 'jev-1.14.0', 'answers': {'category': {
        'type': 'choice', 'choice': selected, 'confidence': .5, 'probabilities': probabilities}},
        'usage': {'input_tokens': 2, 'output_tokens': 1}}
    return payload, reply


def accepted(payload, reply):
    try:
        validate_envelope(json.dumps(payload).encode(), json.dumps(reply).encode())
        return True
    except (ValueError, TypeError, KeyError):
        return False


class ChoiceValidationTests(unittest.TestCase):
    def test_inclusive_one_percent_and_rounding_allowance(self):
        for total, expected in ((.989999999998, False), (.9899999999995, True),
                                (.99, True), (.9900001, True), (1, True),
                                (1.0099999, True), (1.01, True),
                                (1.0100000000005, True), (1.010000000002, False)):
            with self.subTest(total=total):
                self.assertEqual(accepted(*envelope([.6, total - .6])), expected)

    def test_top_choice_production_rounding_allowance(self):
        for delta, expected in ((0, True), (5e-10, True), (1e-9, True), (2e-9, False)):
            with self.subTest(delta=delta):
                self.assertEqual(accepted(*envelope([.5, .5 + delta], 'category_0')), expected)

    def test_malformed_values_membership_and_confidence_remain_rejected(self):
        for value in (-.001, 1.001, float('nan'), float('inf'), True, None, '0.4'):
            with self.subTest(value=value):
                self.assertFalse(accepted(*envelope([.6, value], 'category_0')))
        for mutation in ('missing', 'extra', 'unknown-choice', 'confidence', 'schema'):
            payload, reply = envelope([.6, .4])
            answer = reply['answers']['category']
            if mutation == 'missing':
                del answer['probabilities']['category_1']
            elif mutation == 'extra':
                answer['probabilities']['extra'] = 0
            elif mutation == 'unknown-choice':
                answer['choice'] = 'absent'
            elif mutation == 'confidence':
                answer['confidence'] = 1.001
            else:
                answer['extra'] = 0
            with self.subTest(mutation=mutation):
                self.assertFalse(accepted(payload, reply))


@unittest.skipUnless(shutil.which('dotnet'), '.NET 10 SDK required for production differential check')
class ProductionChoiceParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        root = Path(cls.temp.name)
        source = Path(__file__).resolve().parents[2] / 'src/Application/Classification/CategorySuggestion.cs'
        # Compile the actual policy source, not a copied formula or substitute validator.
        (root / 'Parity.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><Compile Include="''' + source.as_posix() + '''" Link="CategorySuggestion.cs" /></ItemGroup>
</Project>''', encoding='utf-8')
        (root / 'Program.cs').write_text('''using Application.Classification;
using System.Text.Json;
foreach (JsonElement item in JsonDocument.Parse(Console.In.ReadToEnd()).RootElement.EnumerateArray())
{
    Dictionary<string, double> probabilities = item.GetProperty("probabilities").Deserialize<Dictionary<string, double>>()!;
    string selected = item.GetProperty("selected").GetString()!;
    CategorySuggestionRequest request = new("Synthetic item", "synthetic-revision",
        probabilities.Keys.Select(id => new SuggestionCategory(id, id, "Synthetic definition", null)).ToArray());
    CategoryJudgment judgment = new(CategorySuggestionPolicy.Model, request.TaxonomyRevision,
        selected, .9, .5, probabilities, 2, 1);
    CategoryHierarchyJudgment hierarchy = new(CategorySuggestionPolicy.Model, request.TaxonomyRevision,
        .9, new Dictionary<string, CategoryChoiceJudgment> { ["category"] = new(selected, .5, probabilities) }, 2, 1);
    Console.WriteLine(JsonSerializer.Serialize(new[] {
        CategorySuggestionPolicy.Decide(request, judgment, "synthetic").Failure is null,
        CategorySuggestionPolicy.Decide(request, hierarchy, "synthetic").Failure is null }));
}
''', encoding='utf-8')
        result = subprocess.run(['dotnet', 'build', str(root / 'Parity.csproj'), '--nologo', '-v:q'],
                                capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.executable = root / 'bin/Debug/net10.0/Parity.dll'

    def test_totals_permutations_and_top_choice_match_both_production_policies(self):
        vectors = []
        for total in (.989999999998, .9899999999995, .99, 1., 1.01, 1.0100000000005, 1.010000000002):
            vectors.extend(itertools.permutations([.6, .3, total - .9]))
        # Exercise both floating-point neighbours around each effective boundary.
        for boundary in (1 - (.01 + 1e-12), 1 + (.01 + 1e-12)):
            for total in (math.nextafter(boundary, 0), boundary, math.nextafter(boundary, 2)):
                for count in (2, 16, 255):
                    vector = [total - .1] + [.1 / (count - 1)] * (count - 1)
                    vectors.extend((vector, list(reversed(vector))))
        cases = [envelope(vector) for vector in vectors]
        cases.extend(envelope([.5, .5 + delta], 'category_0') for delta in (0, 5e-10, 1e-9, 2e-9))
        inputs = [{'probabilities': reply['answers']['category']['probabilities'],
                   'selected': reply['answers']['category']['choice']} for _, reply in cases]
        result = subprocess.run(['dotnet', str(self.executable)], input=json.dumps(inputs),
                                capture_output=True, text=True, timeout=30, check=True)
        decisions = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(decisions), len(cases))
        for (payload, reply), production in zip(cases, decisions):
            with self.subTest(probabilities=reply['answers']['category']['probabilities']):
                self.assertEqual([accepted(payload, reply)] * 2, production)


if __name__ == '__main__':
    unittest.main()

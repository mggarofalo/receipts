"""Exercise the real Roslyn verifier against disposable, compilable fixtures."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

TOOL = Path(__file__).with_name('DeadCodeAudit.csproj').resolve()

class VerifierTests(unittest.TestCase):
    def probe(self, source='', test_source='', broken=False, absent=False, missing_project=False):
        with tempfile.TemporaryDirectory(prefix='receipts-dead-code-') as directory:
            root = Path(directory)
            (root/'src').mkdir()
            (root/'tests').mkdir()
            (root/'src/P.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
            (root/'src/Subject.cs').write_text('public static class Subject { public static int Unused() => 42; }\n'+source)
            (root/'tests/T.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../src/P.csproj" /></ItemGroup></Project>')
            (root/'tests/Test.cs').write_text(test_source)
            (root/'Fixture.slnx').write_text('<Solution><Project Path="src/P.csproj"/><Project Path="tests/T.csproj"/></Solution>')
            candidate = {'project':'src/P.csproj','symbol':'Subject::Unused','allowedClusterFiles':[],'allowGeneratedMediatorDispatch':True}
            manifest = {'productionProjects':['src/P.csproj']+(['src/Missing.csproj'] if missing_project else []),'candidates':[candidate]}
            (root/'manifest.json').write_text(json.dumps(manifest))
            if broken:
                (root/'src/Broken.cs').write_text('public class Broken { MissingType Missing; }')
            if absent:
                (root/'src/Subject.cs').write_text('public static class Subject { }')
            subprocess.run(['dotnet','restore',str(root/'Fixture.slnx')], check=True, capture_output=True)
            command=['dotnet','run','--no-build','--project',str(TOOL),'--',str(root/'Fixture.slnx'),str(root/'manifest.json')]
            if absent: command.append('--expect-absent')
            return subprocess.run(command,capture_output=True,text=True)

    def test_live_caller_rejected(self):
        result=self.probe(source='public class Caller { public int Run() => Subject.Unused(); }')
        self.assertEqual(1,result.returncode,result.stdout+result.stderr)
        self.assertIn('LIVE-OR-UNCLASSIFIED',result.stdout)

    def test_test_only_reference_admitted(self):
        result=self.probe(test_source='public class Test { public int Run() => Subject.Unused(); }')
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)
        self.assertIn('test tests/Test.cs',result.stdout)

    def test_generated_annotation_does_not_hide_live_caller(self):
        result=self.probe(source='[System.CodeDom.Compiler.GeneratedCode("Mediator.SourceGenerator", "1")] public class Caller { public int Run() => Subject.Unused(); }')
        self.assertEqual(1,result.returncode,result.stdout+result.stderr)
        self.assertIn('LIVE-OR-UNCLASSIFIED',result.stdout)

    def test_incomplete_binding_rejected(self):
        result=self.probe(broken=True)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Incomplete binding',result.stderr)

    def test_missing_project_rejected(self):
        result=self.probe(missing_project=True)
        self.assertNotEqual(0,result.returncode)
        self.assertIn('Required production project missing',result.stderr)

    def test_removed_member_verified(self):
        result=self.probe(absent=True)
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)
        self.assertIn('ABSENT Subject::Unused',result.stdout)

if __name__ == '__main__':
    subprocess.run(['dotnet','build',str(TOOL),'--nologo'],check=True)
    unittest.main()

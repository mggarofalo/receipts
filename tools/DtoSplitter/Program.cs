using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: DtoSplitter <check|split> ...");
	return 1;
}

string command = args[0];

return command switch
{
	"check" => Check(args),
	"split" => Split(args),
	_ => Error($"Unknown command: {command}")
};

static int Check(string[] args)
{
	if (args.Length < 3)
	{
		Console.Error.WriteLine("Usage: DtoSplitter check <specPath> <generatedDir>");
		return 1;
	}

	string specPath = ValidateFilePath(args[1]);
	string generatedDir = ResolvePath(args[2]);

	string specHash = ComputeHash(specPath);

	if (!Directory.Exists(generatedDir))
	{
		Console.WriteLine("No generated files found. Regeneration needed.");
		return 1;
	}

	string[] existingFiles = Directory.GetFiles(generatedDir, "*.g.cs");

	if (existingFiles.Length == 0)
	{
		Console.WriteLine("No generated files found. Regeneration needed.");
		return 1;
	}

	// Every generated file embeds the spec hash it was produced from. Require
	// ALL of them to match the current spec — not just the first. A single stale
	// or orphaned file (a type removed from the spec, or a leftover monolithic
	// Dtos.g.cs that carries no hash line) forces a full regeneration, which
	// clears the directory and rewrites the exact current set. Reading only
	// existingFiles[0] previously let orphaned old-spec files linger and break
	// local builds (RECEIPTS-748).
	foreach (string file in existingFiles)
	{
		string? fileHash = ReadHashFromFile(file);
		if (fileHash != specHash)
		{
			string name = Path.GetFileName(file);
			Console.WriteLine($"Generated file '{name}' is stale ({fileHash?[..8] ?? "no-hash"}... -> {specHash[..8]}...). Regeneration needed.");
			return 1;
		}
	}

	Console.WriteLine("DTOs up-to-date.");
	return 0;
}

static int Split(string[] args)
{
	if (args.Length < 4)
	{
		Console.Error.WriteLine("Usage: DtoSplitter split <specPath> <monolithicFile> <generatedDir>");
		return 1;
	}

	string specPath = ValidateFilePath(args[1]);
	string monolithicFile = ValidateFilePath(args[2]);
	string generatedDir = ResolvePath(args[3]);

	string specHash = ComputeHash(specPath);
	string sourceText = File.ReadAllText(monolithicFile);

	Microsoft.CodeAnalysis.SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText);
	CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

	// Find the namespace declaration (block-scoped with braces, as NSwag uses)
	NamespaceDeclarationSyntax? ns = root.DescendantNodes()
		.OfType<NamespaceDeclarationSyntax>()
		.FirstOrDefault();

	if (ns is null)
	{
		Console.Error.WriteLine("No namespace declaration found in monolithic file.");
		return 1;
	}

	string namespaceName = ns.Name.ToString();

	// Extract the using alias from inside the namespace
	List<string> namespaceUsings = ns.Usings
		.Select(u => u.ToFullString().Trim())
		.ToList();

	// Extract pragma disable lines from the original file
	List<string> pragmaDisables = [];
	List<string> pragmaRestores = [];

	foreach (string line in sourceText.Split('\n'))
	{
		string trimmed = line.Trim().TrimEnd('\r');
		if (trimmed.StartsWith("#pragma warning disable"))
		{
			pragmaDisables.Add(trimmed);
		}
		else if (trimmed.StartsWith("#pragma warning restore"))
		{
			pragmaRestores.Add(trimmed);
		}
	}

	// Extract all type declarations from the namespace (classes, structs, records, enums)
	List<BaseTypeDeclarationSyntax> types = ns.Members
		.OfType<BaseTypeDeclarationSyntax>()
		.ToList();

	if (types.Count == 0)
	{
		Console.Error.WriteLine("No type declarations found in namespace.");
		return 1;
	}

	// Delete all existing *.g.cs files (stale cleanup)
	if (Directory.Exists(generatedDir))
	{
		foreach (string file in Directory.GetFiles(generatedDir, "*.g.cs"))
		{
			File.Delete(file);
		}
	}
	else
	{
		Directory.CreateDirectory(generatedDir);
	}

	// Build the shared preamble and postamble
	string preamble = BuildPreamble(specHash, pragmaDisables, namespaceName, namespaceUsings);
	string postamble = BuildPostamble(pragmaRestores);

	int count = 0;
	foreach (BaseTypeDeclarationSyntax type in types)
	{
		string typeName = type.Identifier.Text;

		// Handle generic types: ApiException<TResult> -> ApiException_TResult
		string fileName = typeName;
		if (type is TypeDeclarationSyntax typeDecl && typeDecl.TypeParameterList is not null)
		{
			string typeParams = string.Join("_", typeDecl.TypeParameterList.Parameters.Select(p => p.Identifier.Text));
			fileName = $"{typeName}_{typeParams}";
		}

		// Get the type text with its leading trivia (attributes, comments)
		string typeText = type.ToFullString();

		StringBuilder sb = new();
		sb.Append(preamble);
		sb.Append(typeText);

		// Ensure the type text ends with a newline before closing brace
		if (!typeText.EndsWith("\r\n"))
		{
			sb.Append("\r\n");
		}

		sb.Append("}\r\n");
		sb.Append("\r\n");
		sb.Append(postamble);

		string outputPath = Path.Combine(generatedDir, $"{fileName}.g.cs");
		string content = sb.ToString();

		// Normalize line endings to CRLF
		content = NormalizeToCrlf(content);

		File.WriteAllText(outputPath, content, new UTF8Encoding(false));
		count++;
	}

	// Delete the monolithic file
	File.Delete(monolithicFile);

	Console.WriteLine($"Split {count} types into individual files in {generatedDir}");
	return 0;
}

static string BuildPreamble(
	string specHash,
	List<string> pragmaDisables,
	string namespaceName,
	List<string> namespaceUsings)
{
	StringBuilder sb = new();
	sb.Append("// <auto-generated>\r\n");
	sb.Append("//     Generated by DtoSplitter from NSwag output. Do not edit.\r\n");
	sb.Append("// </auto-generated>\r\n");
	sb.Append($"// spec-hash: {specHash}\r\n");
	sb.Append("\r\n");
	sb.Append("#nullable enable\r\n");
	sb.Append("\r\n");

	foreach (string pragma in pragmaDisables)
	{
		sb.Append(pragma);
		sb.Append("\r\n");
	}

	sb.Append("\r\n");
	sb.Append($"namespace {namespaceName}\r\n");
	sb.Append("{\r\n");

	foreach (string usingDirective in namespaceUsings)
	{
		sb.Append($"    {usingDirective}\r\n");
	}

	sb.Append("\r\n");
	return sb.ToString();
}

static string BuildPostamble(List<string> pragmaRestores)
{
	StringBuilder sb = new();

	foreach (string pragma in pragmaRestores)
	{
		sb.Append(pragma);
		sb.Append("\r\n");
	}

	return sb.ToString();
}

static string ComputeHash(string filePath)
{
	byte[] fileBytes = File.ReadAllBytes(filePath);
	byte[] hashBytes = SHA256.HashData(fileBytes);
	return Convert.ToHexStringLower(hashBytes);
}

static string? ReadHashFromFile(string filePath)
{
	foreach (string line in File.ReadLines(filePath))
	{
		Match match = Regex.Match(line, @"^// spec-hash: ([0-9a-f]+)$");
		if (match.Success)
		{
			return match.Groups[1].Value;
		}
	}

	return null;
}

static string NormalizeToCrlf(string text)
{
	// Replace any existing CRLF with LF first, then convert all LF to CRLF
	text = text.Replace("\r\n", "\n");
	text = text.Replace("\r", "\n");
	text = text.Replace("\n", "\r\n");
	return text;
}

static string ResolvePath(string path)
{
	string resolved = Path.GetFullPath(path);
	if (resolved.Contains("..", StringComparison.Ordinal) || resolved.Contains('\0'))
	{
		throw new ArgumentException($"Invalid path: {path}");
	}

	return resolved;
}

static string ValidateFilePath(string path)
{
	string resolved = ResolvePath(path);
	if (!File.Exists(resolved))
	{
		throw new FileNotFoundException($"File not found: {resolved}");
	}

	return resolved;
}

static int Error(string message)
{
	Console.Error.WriteLine(message);
	return 1;
}

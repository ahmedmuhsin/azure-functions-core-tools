// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The reader that the tests of the Rust user texts use to find every string in a source file. If it missed strings, those
    // tests would pass for the wrong reason, so it has tests of its own.
    public class CSharpStringLiteralsTests
    {
        [Fact]
        public void Read_RegularStrings_ReturnsTheirText()
        {
            var literals = CSharpStringLiterals.Read("var a = \"one\"; var b = \"two words\";");

            literals.Select(literal => literal.Text).Should().Equal("one", "two words");
        }

        [Fact]
        public void Read_Escapes_AreDecoded()
        {
            var literals = CSharpStringLiterals.Read("var a = \"say \\\"hi\\\"\\nthen \\\\ go\";");

            literals.Should().ContainSingle().Which.Text.Should().Be("say \"hi\"\nthen \\ go");
        }

        [Fact]
        public void Read_VerbatimString_KeepsBackslashesAndDoubledQuotes()
        {
            var literals = CSharpStringLiterals.Read("var a = @\"C:\\dir\\\"\"x\"\"\";");

            literals.Should().ContainSingle().Which.Text.Should().Be("C:\\dir\\\"x\"");
        }

        [Fact]
        public void Read_Comments_AreLeftOut()
        {
            var source = "// \"in a line comment\"\n/* \"in a block\n comment\" */\nvar a = \"real\"; // \"after\"";

            var literals = CSharpStringLiterals.Read(source);

            literals.Select(literal => literal.Text).Should().Equal("real");
        }

        [Fact]
        public void Read_ASlashesInsideAString_DoNotStartAComment()
        {
            var literals = CSharpStringLiterals.Read("var a = \"https://example.com/x\"; var b = \"after\";");

            literals.Select(literal => literal.Text).Should().Equal("https://example.com/x", "after");
        }

        [Fact]
        public void Read_ACharacterLiteralWithAQuote_IsNotTheStartOfAString()
        {
            var literals = CSharpStringLiterals.Read("var q = '\"'; var e = '\\''; var a = \"yes\";");

            literals.Select(literal => literal.Text).Should().Equal("yes");
        }

        [Fact]
        public void Read_InterpolatedString_LeavesOutTheCodeInTheBraces()
        {
            var literals = CSharpStringLiterals.Read("var a = $\"Could not copy '{source}' to '{destination}'. {{kept}}\";");

            literals.Should().ContainSingle().Which.Text.Should().Be("Could not copy '' to ''. {kept}");
        }

        [Fact]
        public void Read_AStringInsideAnInterpolationHole_IsOneMoreLiteral()
        {
            var literals = CSharpStringLiterals.Read("var a = $\"before{(flag ? \" Stop the worker.\" : string.Empty)} after\";");

            literals.Select(literal => literal.Text).Should().BeEquivalentTo(" Stop the worker.", "before after");
        }

        [Fact]
        public void Read_RawString_ReturnsItsContent()
        {
            var literals = CSharpStringLiterals.Read("var a = \"\"\"{\"name\":\"x\"}\"\"\"; var b = \"after\";");

            literals.Select(literal => literal.Text).Should().Equal("{\"name\":\"x\"}", "after");
        }

        [Fact]
        public void Read_LineNumbers_StartWhereTheStringStarts()
        {
            var source = "var a = 1;\nvar b = \"second\";\n\nvar c = @\"third\nfourth\";\nvar d = \"fifth\";";

            var literals = CSharpStringLiterals.Read(source);

            literals.Select(literal => (literal.Line, literal.Text)).Should().Equal((2, "second"), (4, "third\nfourth"), (6, "fifth"));
        }

        [Fact]
        public void Read_AStringThatIsNotClosed_EndsAtTheEndOfTheLine()
        {
            var literals = CSharpStringLiterals.Read("var a = \"open\nvar b = \"closed\";");

            literals.Select(literal => literal.Text).Should().Contain("closed");
        }

        // The tests of the user texts would be empty without this: the real file has to give many strings.
        [Fact]
        public void Read_TheRustHelpers_FindsItsKnownMessages()
        {
            var path = Path.Combine(RepositoryRoot(), "src", "Cli", "func", "Helpers", "RustHelpers.cs");

            var literals = CSharpStringLiterals.Read(File.ReadAllText(path));

            literals.Count.Should().BeGreaterThan(40);
            literals.Should().Contain(literal => literal.Text.Contains("Could not find a Rust toolchain"));
            literals.Should().Contain(literal => literal.Text.Contains("Could not copy the built binary"));
            literals.Should().Contain(literal => literal.Text.Contains("Building Rust worker binary"));
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Azure.Functions.Cli.sln")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir is not null, "Could not find repo root (Azure.Functions.Cli.sln).");
            return dir!.FullName;
        }
    }
}

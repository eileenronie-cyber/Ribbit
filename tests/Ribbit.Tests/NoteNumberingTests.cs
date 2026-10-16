using Ribbit.Geometry.Text;
using Xunit;

namespace Ribbit.Tests;

public class NoteNumberingTests
{
    private static readonly NumberSettings List1 = NumberSettings.Default;
    private static NumberSettings Typed(string fmt = "{n}.", int start = 1, int skip = 1) => new(skip, start, NumberMode.Typed, fmt);

    [Fact]
    public void Header_is_kept_and_body_is_numbered()
    {
        (List<string> h, List<string> b) = NoteNumbering.Renumber("NOTES\rFirst\rSecond", Typed());
        Assert.Equal(["NOTES"], h);
        Assert.Equal(["1.\tFirst", "2.\tSecond"], b);
    }

    [Fact]
    public void Typed_numbers_are_stripped_so_a_rerun_is_safe()
    {
        string once = NoteNumbering.Plan("NOTES\rFirst\rSecond", Typed())!.NewText;
        string twice = NoteNumbering.Plan(once, Typed())!.NewText;
        Assert.Equal(once, twice);
        Assert.Equal("NOTES\r1.\tFirst\r2.\tSecond", once);
    }

    [Theory]
    [InlineData("1. Pipe", "Pipe")]
    [InlineData("12) Pipe", "Pipe")]
    [InlineData("(3) Pipe", "Pipe")]
    [InlineData("4- Pipe", "Pipe")]
    [InlineData("5: Pipe", "Pipe")]
    [InlineData("6.\tPipe", "Pipe")]
    [InlineData("3.5 m cover", "3.5 m cover")]
    [InlineData("2-way valve", "2-way valve")]
    [InlineData("2:1 ratio", "2:1 ratio")]
    public void What_counts_as_a_typed_number(string line, string kept)
    {
        (_, List<string> b) = NoteNumbering.Renumber("H\r" + line, List1);
        Assert.Equal([kept], b);
    }

    [Fact]
    public void A_custom_format_is_stripped_too()
    {
        (_, List<string> b) = NoteNumbering.Renumber("H\rN1 = Pipe", Typed("N{n} =") with { Mode = NumberMode.List });
        Assert.Equal(["Pipe"], b);
    }

    [Fact]
    public void An_unnumbered_line_in_a_numbered_note_joins_the_item_above_with_no_space_after_a_hyphen()
    {
        (_, List<string> b) = NoteNumbering.Renumber("NOTES\r1. Refer to ABC-DWG-\rME-03-100\r2. Second\rcontinues here", List1);
        Assert.Equal(["Refer to ABC-DWG-ME-03-100", "Second continues here"], b);
    }

    [Fact]
    public void A_note_with_no_typed_numbers_is_one_item_per_paragraph()
    {
        (_, List<string> b) = NoteNumbering.Renumber("NOTES\rOne\rTwo\rThree", List1);
        Assert.Equal(["One", "Two", "Three"], b);
    }

    [Fact]
    public void Soft_breaks_join_unless_the_next_piece_is_numbered()
    {
        (_, List<string> b) = NoteNumbering.Renumber("NOTES\rFirst part\vsecond part\v2. Next", List1);
        Assert.Equal(["First part second part", "Next"], b);
    }

    [Fact]
    public void Invisible_characters_do_not_hide_a_number()
    {
        (_, List<string> b) = NoteNumbering.Renumber("NOTES\r​1. Pipe\r﻿2. Duct", List1);
        Assert.Equal(["Pipe", "Duct"], b);
    }

    [Fact]
    public void The_blank_line_under_the_header_stays()
    {
        NotePlan p = NoteNumbering.Plan("NOTES\r\rFirst\rSecond", List1)!;
        Assert.Equal("NOTES\r\rFirst\rSecond", p.NewText);
        Assert.Equal(7, p.BodyStart);
        Assert.Equal(5, p.NewHeaderLength);
        Assert.True(p.TextUnchanged);
    }

    [Fact]
    public void Body_start_skips_the_header()
    {
        NotePlan p = NoteNumbering.Plan("GENERAL NOTES\r1. a\r2. b", List1)!;
        Assert.Equal("GENERAL NOTES\ra\rb", p.NewText);
        Assert.Equal(14, p.BodyStart);
        Assert.False(p.TextUnchanged);
    }

    [Fact]
    public void No_header_means_the_list_starts_at_zero()
    {
        NotePlan p = NoteNumbering.Plan("a\rb", List1 with { SkipLines = 0 })!;
        Assert.Equal(0, p.BodyStart);
        Assert.Equal("a\rb", p.NewText);
    }

    [Fact]
    public void Trailing_paragraph_mark_does_not_count_as_a_change()
        => Assert.True(NoteNumbering.Plan("NOTES\ra\rb\r", List1)!.TextUnchanged);

    [Fact]
    public void Only_a_header_has_no_body()
        => Assert.Null(NoteNumbering.Plan("NOTES", List1));

    [Fact]
    public void Start_number_and_format()
    {
        NotePlan p = NoteNumbering.Plan("H\ra\rb", Typed("({n})", start: 7))!;
        Assert.Equal("H\r(7)\ta\r(8)\tb", p.NewText);
    }

    [Fact]
    public void Remove_mode_strips_numbers_and_types_none()
    {
        NotePlan p = NoteNumbering.Plan("H\r1.\ta\r2.\tb", new NumberSettings(1, 1, NumberMode.Remove, "{n}."))!;
        Assert.Equal("H\ra\rb", p.NewText);
    }

    [Fact]
    public void A_gap_in_the_typed_numbers_is_reported_with_the_suspect_line()
    {
        string w = NoteNumbering.NumberWarning("NOTES\r1. one\r2. two\r 3 three\r4. four", 1, "{n}.");
        Assert.StartsWith("Typed number(s) 3 not recognised", w);
        Assert.Contains("' 3 three'", w);
    }

    [Fact]
    public void No_gap_no_warning()
    {
        Assert.Equal("", NoteNumbering.NumberWarning("NOTES\r1. one\r2. two", 1, "{n}."));
        Assert.Equal("", NoteNumbering.NumberWarning("NOTES\r5. one\r6. two", 1, "{n}."));
    }

    [Fact]
    public void The_preview_can_be_edited_and_read_back_to_the_same_text()
    {
        NumberSettings[] all = [List1, Typed(), Typed("{n})", 3), new(1, 1, NumberMode.Remove, "{n}.")];
        foreach (NumberSettings s in all)
        {
            string plain = "GENERAL NOTES\r\r1. Refer to ABC-DWG-\rME-03\r2. All pipes 3.5 m deep";
            string preview = NoteNumbering.PreviewText(plain, s);
            if (s.Mode != NumberMode.Remove)
                Assert.Equal(preview, NoteNumbering.PreviewText(preview, s, join: false));
            NotePlan direct = NoteNumbering.Plan(plain, s)!;
            NotePlan viaEdit = NoteNumbering.Plan(plain, s, preview)!;
            Assert.Equal(direct.NewText, viaEdit.NewText);
        }
    }

    [Fact]
    public void Preview_numbers_every_mode()
    {
        Assert.Equal("H\n1.  a\n2.  b", NoteNumbering.PreviewText("H\ra\rb", List1));
        Assert.Equal("H\n4)  a\n5)  b", NoteNumbering.PreviewText("H\ra\rb", Typed("{n})", 4)));
        Assert.Equal("H\na\nb", NoteNumbering.PreviewText("H\r1. a\r2. b", new(1, 1, NumberMode.Remove, "{n}.")));
    }

    [Theory]
    [InlineData("{n}.", true)]
    [InlineData("N{n}", true)]
    [InlineData("n.", false)]
    [InlineData("{n}{n}", false)]
    [InlineData("{n}}", false)]
    [InlineData("", false)]
    public void Format_must_hold_one_placeholder(string fmt, bool ok)
        => Assert.Equal(ok, NoteNumbering.FormatOk(fmt));

    [Fact]
    public void Settings_validation()
    {
        Assert.Null(NoteNumbering.Validate(List1));
        Assert.NotNull(NoteNumbering.Validate(List1 with { SkipLines = -1 }));
        Assert.NotNull(NoteNumbering.Validate(Typed("bad")));
        Assert.Null(NoteNumbering.Validate(List1 with { Format = "bad" }));   // list mode ignores the format
    }

    [Fact]
    public void Old_header_and_body_ranges_point_at_the_old_text()
    {
        string old = "NOTES\r1. a\r2. b\r";
        NotePlan p = NoteNumbering.Plan(old, List1)!;
        Assert.Equal(0, p.OldHeaderStart);
        Assert.Equal(5, p.OldHeaderLength);
        Assert.Equal(6, p.OldBodyStart);
        Assert.Equal(old.Length - 1, p.OldBodyEnd);
    }

    [Fact]
    public void More_header_lines_than_paragraphs_is_no_body()
        => Assert.Null(NoteNumbering.Plan("a\rb", List1 with { SkipLines = 5 }));
}

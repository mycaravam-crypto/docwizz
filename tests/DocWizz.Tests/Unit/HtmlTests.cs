namespace DocWizz.Tests.Unit;

public class HtmlTests
{
    static readonly Dictionary<string, string> Pages = new()
    {
        ["index.md"] = "demo", ["api.md"] = "API endpoints", ["architecture-description.md"] = "Architecture description",
        ["modules/src-Core.md"] = "src/Core", ["modules/src-Web.md"] = "src/Web", ["views/context.md"] = "System context",
    };

    [Fact]
    public void Links_to_generated_pages_become_html_and_source_links_stay()
    {
        var html = Html.Page("modules/src-Core.md",
            "# src/Core & co\n\nSee [API](../api.md#get-orders), [notes](../notes.md) and [source](../../src/Core/Order.cs).\n", Pages);
        Assert.Contains("href=\"../api.html#get-orders\"", html);
        Assert.Contains("href=\"../notes.md\"", html);
        Assert.Contains("href=\"../../src/Core/Order.cs\"", html);
        Assert.Contains("<title>src/Core &amp; co</title>", html);
    }

    [Fact]
    public void Tables_have_independent_filter_inputs_and_row_counts()
    {
        var html = Html.Page("index.md",
            "# Report\n\n| A | B |\n|---|---|\n| one | two |\n\n| C |\n|---|\n| three |\n", Pages);
        Assert.Contains("id=\"table-filter-1\"", html);
        Assert.Contains("id=\"table-filter-2\"", html);
        Assert.Contains("data-table-filter", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("rows.forEach(row => { row.hidden = true; });", html);
    }

    [Fact]
    public void Data_tables_have_shared_sorting_pagination_and_scroll_controls()
    {
        var html = Html.Page("index.md",
            "# Report\n\n| ID | Name |\n|---|---|\n| 10 | Alpha |\n| 2 | Beta |\n", Pages);
        Assert.Contains("data-table-filter", html);
        Assert.Contains("data-table-size", html);
        Assert.Contains("data-table-prev", html);
        Assert.Contains("data-table-next", html);
        Assert.Contains("aria-sort", html);
        Assert.Contains("table-long", html);
        Assert.Contains("localeCompare", html);
        Assert.Contains("tbody.appendChild(row)", html);
        Assert.Contains("rows.length <= 25", html);
    }

    [Fact]
    public void Tables_scroll_inside_a_wrapper()
    {
        var html = Html.Page("index.md", "# demo\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", Pages);
        Assert.Contains("<div class=\"table-wrap\"><table>", html);
        Assert.Contains("</table></div>", html);
    }

    [Theory]
    [InlineData("Größenprüfer")]
    [InlineData("BestellÜbersicht")]
    [InlineData("my_func")]
    [InlineData("Order")]
    public void Symbol_anchors_match_the_rendered_heading_ids(string name)
    {
        var html = Html.Page("modules/src-Core.md", $"# src/Core\n\n### {name}\n", Pages);
        Assert.Contains($"<h3 id=\"{Naming.Anchor(name)}\">", html);
    }

    [Theory]
    [InlineData("Foo<T>")]
    [InlineData("Grid of rows typed as Array<Row> with a <slot> per cell.")]
    [InlineData("a <b>bold</b> tag")]
    public void Angle_brackets_in_names_and_docs_stay_text(string text)
    {
        var html = Html.Page("modules/src-Core.md", $"# src/Core\n\n### {Generator.Text(text)}\n\n{Generator.Text(text)}\n", Pages);
        var encoded = System.Net.WebUtility.HtmlEncode(text).Replace("&#39;", "'");
        Assert.Contains($"<h3 id=\"{Naming.Anchor(text)}\">{encoded}</h3>", html);
        Assert.Contains($"<p>{encoded}</p>", html);
    }

    [Fact]
    public void Code_spans_keep_their_angle_brackets_unescaped() =>
        Assert.Equal("returns `List<T>` or Map\\<K>", Generator.Text("returns `List<T>` or Map<K>"));

    // #251: Text's idea of a code span must match CommonMark's, or a `<` it leaves alone reaches the page as a tag.
    [Theory]
    [InlineData("\\`<img src=x onerror=alert(1)>\\`")]
    [InlineData("`<img src=x onerror=alert(1)>``")]
    [InlineData("``<img src=x onerror=alert(1)>`")]
    public void Backticks_that_open_no_code_span_leave_angle_brackets_escaped(string text)
    {
        Assert.Contains("\\<img", Generator.Text(text));
        var html = Html.Page("modules/src-Core.md", $"# src/Core\n\n{Generator.Text(text)}\n", Pages);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void Raw_html_in_markdown_renders_as_text_but_table_line_breaks_stay()
    {
        var html = Html.Page("index.md", "# demo\n\n<script>alert(1)</script>\n\n| A |\n|---|\n| one<br>two <img src=x onerror=alert(1)> |\n", Pages);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("one<br>two", html);
    }

    [Theory]
    [InlineData("Prüfung/Übersicht", "Pruefung-Uebersicht")]
    [InlineData("src/Größe", "src-Groesse")]
    [InlineData("src/Café", "src-Cafe")]
    [InlineData("src/Core", "src-Core")]
    public void Module_page_names_spell_out_umlauts(string folder, string slug) =>
        Assert.Equal(slug, Naming.Slug(folder));

    [Fact]
    public void Sidebar_reaches_every_page_relative_to_the_current_one()
    {
        var html = Html.Page("modules/src-Core.md", "# src/Core\n", Pages);
        foreach (var page in Pages.Keys)
            Assert.Contains($"href=\"../{Path.ChangeExtension(page, ".html")}\"", html);
        Assert.Contains("href=\"../modules/src-Core.html\" aria-current=\"page\"", html);
        // The group holding the current page is open, the others closed.
        Assert.Contains("<details open><summary>Code</summary>", html);
        Assert.Contains("<details><summary>Architecture</summary>", html);

        var index = Html.Page("index.md", "# demo\n", Pages);
        Assert.Contains("href=\"modules/src-Web.html\"", index);
        Assert.Contains("href=\"index.html\" aria-current=\"page\"", index);
        Assert.Contains("<details><summary>Code</summary>", index);
    }

    [Fact]
    public void Test_page_is_nested_under_quality_in_both_navigation_regions()
    {
        var pages = new Dictionary<string, string>(Pages)
        {
            ["quality.md"] = "Quality", ["tests.md"] = "Test traceability"
        };
        var html = Html.Page("tests.md", "# Test traceability\n", pages);
        Assert.Contains("<details open><summary>Quality</summary>", html);
        Assert.Contains("href=\"tests.html\" aria-current=\"page\"", html);
        Assert.Contains("href=\"quality.html\" aria-current=\"true\"", html);
        Assert.Contains("href=\"quality.html\">Quality</a>", html);
    }

    [Fact]
    public void Lists_the_sections_of_pages_with_three_or_more()
    {
        var many = Html.Page("api.md", "# API endpoints\n\n## Orders\n\n## Two `words`\n\n## Users\n", Pages);
        Assert.Contains("On this page", many);
        Assert.Contains("class=\"toc-inner\"", many);
        Assert.Contains("<a href=\"#orders\">Orders</a>", many);
        Assert.Contains("<a href=\"#two-words\">Two words</a>", many);
        Assert.Contains("<a href=\"#users\">Users</a>", many);

        var few = Html.Page("api.md", "# API endpoints\n\n## Orders\n\n## Users\n", Pages);
        Assert.Contains("On this page", few);
    }

    [Fact]
    public void Redesigned_pages_have_navigation_theme_controls_and_offline_search()
    {
        var html = Html.Page("modules/src-Core.md", "# Core\n\n## Intro\n\n## Details\n", Pages);
        Assert.Contains("id=\"side-filter\"", html);
        Assert.Contains("id=\"theme-toggle\"", html);
        Assert.Contains("id=\"nav-toggle\"", html);
        Assert.Contains("class=\"breadcrumbs\"", html);
        Assert.Contains("id=\"reading-progress\"", html);
        Assert.Contains("prefers-reduced-motion", html);
        Assert.Contains("aria-label=\"On this page\"", html);
        Assert.Contains("src=\"../search.js\"", html);
    }

    [Fact]
    public void Mermaid_is_pinned_and_search_is_a_combobox()
    {
        var html = Html.Page("index.md", "# demo\n", Pages);
        Assert.Matches(@"mermaid@\d+\.\d+\.\d+/", html);
        Assert.Contains("role=\"combobox\"", html);
        Assert.Contains("href=\"#content\"", html);
        Assert.Contains("@media print", html);
    }

    // So upgrading docwizz visibly refreshes every HTML page, and a reader can tell which build produced it.
    [Fact]
    public void Every_page_names_the_docwizz_version_that_generated_it()
    {
        var html = Html.Page("index.md", "# demo\n", Pages);
        Assert.Contains($"<footer class=\"generator\">Generated by docwizz {AppVersion.Number}</footer>", html);
    }

    [Fact]
    public void Title_falls_back_to_the_file_name()
    {
        Assert.Equal("Quality", Html.Title("quality.md", "<!-- marker -->\n# Quality\n\ntext"));
        Assert.Equal("context", Html.Title("views/context.md", "no heading"));
    }

    [Fact]
    public void Same_pages_in_any_order_give_the_same_html()
    {
        var reversed = Pages.Reverse().ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(Html.Page("index.md", "# demo\n", Pages), Html.Page("index.md", "# demo\n", reversed));
    }
}

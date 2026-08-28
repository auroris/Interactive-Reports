using InteractiveReport.Core.Model;
using InteractiveReport.Core.Validation;
using static InteractiveReport.Core.Tests.TestFixtures;

namespace InteractiveReport.Core.Tests;

public class StateValidatorTests
{
    private static ValidatedState Validate(ReportState state, ReportDefinition? def = null)
        => StateValidator.Validate(def ?? OrdersDefinition(ReportDialect.Sqlite), state, OrdersSchema);

    [Fact]
    public void Old_state_versions_are_rejected_with_a_precise_error()
    {
        foreach (var version in new[] { 1, 2 })
        {
            var ex = Assert.Throws<ReportValidationException>(() =>
                Validate(new ReportState { V = version }));

            Assert.Contains(ex.Errors, error => error.Path == "v" && error.Message ==
                $"state version {version} is not supported; this server requires version 3 pipeline documents");
        }
    }

    [Fact]
    public void Unknown_filter_column_is_a_precise_expression_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Filters = [Filter("NO_SUCH = 1")] })));

        Assert.Contains(ex.Errors, error =>
            error.Path == "pipeline[0].layer.filters[0].expr" && error.Message.Contains("NO_SUCH"));
    }

    [Fact]
    public void Text_operator_on_number_column_is_a_validation_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Filters = [Filter("CONTAINS(AMOUNT, '12')")] })));

        Assert.Contains(ex.Errors, e =>
            e.Path == "pipeline[0].layer.filters[0].expr" && e.Message.Contains("must be text"));
    }

    [Fact]
    public void Between_requires_two_element_array()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Filters = [Filter("AMOUNT BETWEEN 1")] })));

        Assert.Contains(ex.Errors, e => e.Message.Contains("expected AND"));
    }

    [Fact]
    public void Comparison_without_value_points_at_blank_operators()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Filters = [Filter("STATUS = NULL")] })));

        Assert.Contains(ex.Errors, e => e.Message.Contains("use IS NULL"));
    }

    [Fact]
    public void Untypeable_value_is_precise_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Filters = [Filter("AMOUNT > 'not-a-number'")] })));

        Assert.Contains(ex.Errors, e =>
            e.Path == "pipeline[0].layer.filters[0].expr" && e.Message.Contains("number and text"));
    }

    [Fact]
    public void Blank_needs_no_value_and_passes()
    {
        var result = Validate(Doc(source: new StageLayer { Filters = [Filter("NOTES IS NULL OR NOTES = ''")] }));

        var rule = Assert.Single(result.Rules.RowPredicates);
        Assert.NotNull(rule.Expression.Ast);
    }

    [Fact]
    public void Disabled_expression_rules_remain_state_but_leave_the_execution_plan()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Filters = [new FilterRule { Enabled = false, Expr = "REMOVED_COLUMN = 1" }],
            Computed = [new ComputedColumn { Enabled = false, Id = "invalid", Expr = "also invalid" }],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "", Enabled = false, Scope = "diagonal", Expr = "also invalid",
                },
            ],
        }));

        Assert.Empty(result.Rules.Definitions);
        Assert.Empty(result.Rules.RowPredicates);
        Assert.Empty(result.Rules.Decorations);
    }

    [Fact]
    public void Page_size_is_clamped_to_max_and_index_to_one()
    {
        var result = Validate(Doc(page: new PageRequest { Index = -3, Size = 99999 }));

        Assert.Equal(1, result.PageIndex);
        Assert.Equal(1000, result.PageSize);
        Assert.False(result.PageAll);
    }

    [Fact]
    public void Page_size_zero_is_the_allow_list_value_for_all_rows()
    {
        var result = Validate(Doc(page: new PageRequest { Index = 9, Size = 0 }));

        Assert.Equal(1, result.PageIndex);
        Assert.Equal(0, result.PageSize);
        Assert.True(result.PageAll);
    }

    [Fact]
    public void Default_sorts_apply_when_state_has_no_pipeline()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = Doc(source: new StageLayer
        {
            Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
        });

        var result = Validate(new ReportState(), def);

        var sort = Assert.Single(result.Sorts);
        Assert.Equal("ORDER_DATE", sort.Column.Name);
        Assert.Equal(SortDir.Desc, sort.Dir);
    }

    [Fact]
    public void Request_pipeline_replaces_the_default_pipeline_wholesale()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = Doc(source: new StageLayer
        {
            Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
            Filters = [Filter("AMOUNT > 1000")],
        });

        // A present pipeline replaces the default's stage array entirely: no per-field
        // merging, so the default's sorts and filters are gone even though the request's
        // source layer never mentions them.
        var bare = Validate(Doc(), def);
        Assert.Empty(bare.Sorts);
        Assert.Empty(bare.Rules.RowPredicates);

        var explicitEmpty = Validate(Doc(source: new StageLayer { Sorts = [] }), def);
        Assert.Empty(explicitEmpty.Sorts);
    }

    [Fact]
    public void Client_sorts_override_defaults_and_unknown_sort_is_ignored()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = Doc(source: new StageLayer
        {
            Sorts = [new SortRule { Col = "ORDER_DATE", Dir = SortDir.Desc }],
        });

        var result = Validate(Doc(source: new StageLayer
        {
            Sorts =
            [
                new SortRule { Col = "AMOUNT", Dir = SortDir.Desc, Nulls = NullPlacement.Last },
                new SortRule { Col = "GONE" },
            ],
        }), def);

        var sort = Assert.Single(result.Sorts);
        Assert.Equal("AMOUNT", sort.Column.Name);
        Assert.Equal(SortDir.Desc, sort.Dir);
        Assert.Equal(NullPlacement.Last, sort.Nulls);
        Assert.Contains(result.Ignored, i => i.Kind == "sort" && i.Detail.Contains("GONE"));
    }

    [Fact]
    public void Column_selection_preserves_request_order_and_drops_unknown()
    {
        var result = Validate(Doc(source: new StageLayer { Columns = ["AMOUNT", "GHOST", "CUSTOMER"] }));

        Assert.Equal(["AMOUNT", "CUSTOMER"], result.SelectColumns.Select(c => c.Name));
        Assert.Contains(result.Ignored, i => i.Kind == "column" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Renderer_sources_are_schema_bound_projection_columns_not_display_columns()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["CUSTOMER"],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat
                {
                    DisplayAs = "link",
                    UrlColumn = "NOTES",
                    TextColumn = "STATUS",
                },
            },
        }));

        Assert.Equal(["CUSTOMER"], result.SelectColumns.Select(c => c.Name));
        Assert.Equal(["CUSTOMER", "NOTES", "STATUS"], result.ProjectionColumns.Select(c => c.Name));
    }

    [Fact]
    public void Unknown_renderer_sources_are_ignored_before_query_composition()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["CUSTOMER"],
            Formats = new()
            {
                ["CUSTOMER"] = new ColumnFormat { DisplayAs = "image", UrlColumn = "GHOST" },
            },
        }));

        Assert.Equal(["CUSTOMER"], result.ProjectionColumns.Select(c => c.Name));
        Assert.Contains(result.Ignored, i => i.Kind == "format" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Action_key_is_a_projection_column_not_a_display_column()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["STATUS"],
            Formats = new()
            {
                ["STATUS"] = new ColumnFormat { DisplayAs = "action", Command = "open", KeyColumn = "ORDER_ID" },
            },
        }));

        Assert.Equal(["STATUS"], result.SelectColumns.Select(c => c.Name));
        Assert.Equal(["STATUS", "ORDER_ID"], result.ProjectionColumns.Select(c => c.Name));
    }

    [Fact]
    public void Unknown_action_key_is_ignored_before_query_composition()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["STATUS"],
            Formats = new()
            {
                ["STATUS"] = new ColumnFormat { DisplayAs = "action", Command = "open", KeyColumn = "GHOST" },
            },
        }));

        Assert.Equal(["STATUS"], result.ProjectionColumns.Select(c => c.Name));
        Assert.Contains(result.Ignored, i => i.Kind == "format" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Action_without_key_binds_nothing_and_never_reads_link_sources()
    {
        // Unlike link/image, a blank key has no fallback-to-self, and url/text
        // sources belong to other renderers even when present on the format.
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["STATUS"],
            Formats = new()
            {
                ["STATUS"] = new ColumnFormat { DisplayAs = "action", Command = "open", UrlColumn = "NOTES", TextColumn = "CUSTOMER" },
            },
        }));

        Assert.Equal(["STATUS"], result.ProjectionColumns.Select(c => c.Name));
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public void Labels_resolve_at_ingestion_but_never_touch_query_surfaces()
    {
        // Unknown keys included on purpose: the map is display state, not a program —
        // resolved for consumers like export, never validated or applied to the schema.
        var result = Validate(Doc(source: new StageLayer
        {
            Labels = new() { ["amount"] = "  Order Total  ", ["GHOST"] = "Also Fine", ["NOTES"] = " " },
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
        }));

        Assert.Equal("Amount", result.Schema.Lookup["AMOUNT"].Label);
        Assert.Equal("Amount", Assert.Single(result.Aggregates).Column.Label);
        Assert.Empty(result.Ignored);
        Assert.Equal("Order Total", result.Labels["AMOUNT"]);   // trimmed, case-insensitive lookup
        Assert.Equal(2, result.Labels.Count);                   // blank-valued entry dropped
    }

    [Fact]
    public void Label_resolution_layers_request_over_default_state_over_column_labels()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.ColumnLabels = new() { ["AMOUNT"] = "Configured" };

        Assert.Equal("Configured", Validate(new ReportState(), def).Labels["AMOUNT"]);

        def.DefaultState = Doc(source: new StageLayer { Labels = new() { ["AMOUNT"] = "Default Report" } });
        Assert.Equal("Default Report", Validate(new ReportState(), def).Labels["AMOUNT"]);

        var request = Validate(Doc(source: new StageLayer { Labels = new() { ["AMOUNT"] = "Mine" } }), def);
        Assert.Equal("Mine", request.Labels["AMOUNT"]);

        // A request pipeline whose source layer has no labels replaces the default
        // pipeline wholesale, so resolution falls to the definition's columnLabels.
        Assert.Equal("Configured", Validate(Doc(), def).Labels["AMOUNT"]);

        // An explicit empty map is a clear, not an inherit.
        Assert.Empty(Validate(Doc(source: new StageLayer { Labels = new() }), def).Labels);
    }

    [Fact]
    public void Display_labels_apply_to_every_metadata_surface_on_request()
    {
        var validated = Validate(Doc(source: new StageLayer
        {
            Labels = new() { ["AMOUNT"] = "Order Total", ["REGION"] = "Territory" },
            Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum }],
            Breaks = ["REGION"],
        }));

        var display = validated.WithDisplayLabels();

        Assert.Equal("Order Total", display.Schema.Lookup["AMOUNT"].Label);
        Assert.Equal("Order Total", display.SelectColumns.Single(c => c.Name == "AMOUNT").Label);
        Assert.Equal("Order Total", Assert.Single(display.Aggregates).Column.Label);
        Assert.Equal("Territory", Assert.Single(display.Breaks).Label);
        Assert.Equal("AMOUNT", display.SelectColumns.Single(c => c.Name == "AMOUNT").Name);   // names untouched
        Assert.Equal("Amount", validated.Schema.Lookup["AMOUNT"].Label);                      // original untouched
    }

    [Fact]
    public void Aggregates_validate_and_dedupe()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Aggregates =
            [
                new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Sum },
                new AggregateRule { Col = "amount", Fn = AggregateFn.Sum },   // dupe, case-insensitive
                new AggregateRule { Col = "GHOST", Fn = AggregateFn.Sum },    // unknown → ignored
            ],
        }));

        var agg = Assert.Single(result.Aggregates);
        Assert.Equal(("AMOUNT", AggregateFn.Sum), (agg.Column.Name, agg.Fn));
        Assert.Contains(result.Ignored, i => i.Kind == "aggregate" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Sum_on_text_column_is_a_validation_error()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Aggregates = [new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.Sum }],
            })));

        Assert.Contains(ex.Errors, e =>
            e.Path == "pipeline[0].layer.aggregates[0]" && e.Message.Contains("text column"));
    }

    [Fact]
    public void Min_max_allowed_on_text_and_dates_but_count_on_anything()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Aggregates =
            [
                new AggregateRule { Col = "CUSTOMER", Fn = AggregateFn.Min },
                new AggregateRule { Col = "ORDER_DATE", Fn = AggregateFn.Max },
                new AggregateRule { Col = "NOTES", Fn = AggregateFn.Count },
            ],
        }));

        Assert.Equal(3, result.Aggregates.Count);
    }

    [Fact]
    public void Break_columns_are_forced_into_the_selection()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Columns = ["AMOUNT"],
            Breaks = ["REGION", "GHOST"],
        }));

        Assert.Equal(["AMOUNT", "REGION"], result.SelectColumns.Select(c => c.Name));
        var b = Assert.Single(result.Breaks);
        Assert.Equal("REGION", b.Name);
        Assert.Contains(result.Ignored, i => i.Kind == "break" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Case_insensitive_column_matching()
    {
        var result = Validate(Doc(source: new StageLayer { Filters = [Filter("status = 'SHIPPED'")] }));

        var rule = Assert.Single(result.Rules.RowPredicates);
        var comparison = Assert.IsType<InteractiveReport.Core.Expressions.Comparison>(rule.Expression.Ast);
        Assert.Equal("STATUS", Assert.IsType<InteractiveReport.Core.Expressions.ColumnRef>(comparison.Left).Column.Name);
    }

    [Fact]
    public void Computed_columns_join_the_effective_schema_for_everything_downstream()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "c1", Label = "Double", Expr = "AMOUNT * 2" }],
            Filters = [Filter("c1 > 100")],
            Sorts = [new SortRule { Col = "c1", Dir = SortDir.Desc }],
            Aggregates = [new AggregateRule { Col = "c1", Fn = AggregateFn.Sum }],
        }));

        Assert.Equal("c1", Assert.Single(result.Rules.Definitions).Effect.Column.Name);
        var condition = Assert.IsType<InteractiveReport.Core.Expressions.Comparison>(
            Assert.Single(result.Rules.RowPredicates).Expression.Ast);
        Assert.Equal("c1", Assert.IsType<InteractiveReport.Core.Expressions.ColumnRef>(condition.Left).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Sorts).Column.Name);
        Assert.Equal("c1", Assert.Single(result.Aggregates).Column.Name);
        Assert.Contains(result.SelectColumns, c => c.Name == "c1" && c.IsComputed && c.Label == "Double");
    }

    [Fact]
    public void Expression_rules_compile_into_typed_effect_phases()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
            Filters = [Filter("c1 > 100")],
            Highlights =
            [
                new HighlightRule
                {
                    Id = "h1",
                    Scope = "cell",
                    Col = "c1",
                    Expr = "c1 > 200",
                    Style = new HighlightStyle { Bg = "gold" },
                },
            ],
        }));

        var definition = Assert.Single(result.Rules.Definitions);
        Assert.Equal(ColumnKind.Number, definition.Expression.Kind);
        Assert.Equal("c1", definition.Effect.Column.Name);

        var predicate = Assert.Single(result.Rules.RowPredicates);
        Assert.Equal(ColumnKind.Bool, predicate.Expression.Kind);

        var decoration = Assert.Single(result.Rules.Decorations);
        Assert.Equal(ColumnKind.Bool, decoration.Expression.Kind);
        Assert.Equal("c1", decoration.Effect.Column!.Name);
    }

    [Fact]
    public void Computed_id_rules_are_enforced()
    {
        var bad = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer { Computed = [new ComputedColumn { Id = "x1", Expr = "1" }] })));
        Assert.Contains(bad.Errors, e =>
            e.Path == "pipeline[0].layer.computed[0]" && e.Message.Contains("must match c1"));

        var dupe = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Computed =
                [
                    new ComputedColumn { Id = "c1", Expr = "1" },
                    new ComputedColumn { Id = "c1", Expr = "2" },
                ],
            })));
        Assert.Contains(dupe.Errors, e => e.Message.Contains("duplicate"));
    }

    [Fact]
    public void Computed_id_shadowing_a_schema_column_is_rejected()
    {
        var schemaWithC1 = OrdersSchema.Append(Col("C1", typeof(string))).ToList();
        var ex = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                Doc(source: new StageLayer { Computed = [new ComputedColumn { Id = "c1", Expr = "1" }] }),
                schemaWithC1));

        Assert.Contains(ex.Errors, e => e.Message.Contains("shadows"));
    }

    [Fact]
    public void Bad_expression_is_a_precise_error_at_the_expr_path()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT +" }],
            })));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.computed[0].expr");
    }

    [Fact]
    public void Highlights_validate_scope_expression_and_resilience()
    {
        var result = Validate(Doc(source: new StageLayer
        {
            Highlights =
            [
                new HighlightRule
                {
                    Id = "h1", Name = "Large order", Sequence = 50, Scope = "row",
                    Expr = "AMOUNT > 1000", Style = new HighlightStyle { Bg = "#fff3cd" },
                },
                new HighlightRule
                {
                    Id = "h2", Scope = "cell", Col = "GONE_COLUMN",
                    Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "#fff3cd" },
                },
                new HighlightRule
                {
                    Id = "h3", Scope = "row", Enabled = false,
                    Expr = "GONE_TOO = 1",
                },
            ],
        }));

        var valid = Assert.Single(result.Rules.Decorations);
        Assert.Equal("h1", valid.Effect.Id);
        Assert.Equal("Large order", valid.Effect.Name);
        Assert.Equal(50, valid.Effect.Sequence);
        Assert.Single(result.Ignored, i => i.Kind == "highlight");
    }

    [Fact]
    public void Highlight_structural_problems_are_errors()
    {
        var badScope = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "diagonal", Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "red" } }],
            })));
        Assert.Contains(badScope.Errors, e => e.Message.Contains("'row' or 'cell'"));

        var badCondition = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "row", Expr = "AMOUNT + 1", Style = new HighlightStyle { Bg = "red" } }],
            })));
        Assert.Contains(badCondition.Errors, e => e.Path == "pipeline[0].layer.highlights[0].expr");

        var noColor = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Highlights = [new HighlightRule { Id = "h1", Scope = "row", Expr = "AMOUNT > 1" }],
            })));
        Assert.Contains(noColor.Errors, e => e.Path == "pipeline[0].layer.highlights[0].style");

        var duplicateSequence = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(source: new StageLayer
            {
                Highlights =
                [
                    new HighlightRule { Id = "h1", Sequence = 10, Expr = "AMOUNT > 1", Style = new HighlightStyle { Bg = "red" } },
                    new HighlightRule { Id = "h2", Sequence = 10, Expr = "AMOUNT > 2", Style = new HighlightStyle { Bg = "blue" } },
                ],
            })));
        Assert.Contains(duplicateSequence.Errors, e => e.Path == "pipeline[0].layer.highlights[1].sequence");
    }

    // ---- pipeline shape ----

    [Fact]
    public void Pipeline_not_starting_with_source_is_a_precise_error()
    {
        var state = new ReportState
        {
            Pipeline = [Group(by: ["REGION"])],
        };

        var ex = Assert.Throws<ReportValidationException>(() => Validate(state));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].shape.kind"
            && e.Message.Contains("must be 'source'") && e.Message.Contains("'group'"));
    }

    [Fact]
    public void Spread_without_a_group_stage_is_an_unsupported_pipeline_shape()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail: [Spread(cols: ["STATUS"])])));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline"
            && e.Message.Contains("unsupported pipeline shape [source, spread]"));
    }

    [Fact]
    public void Chart_after_group_is_an_unsupported_pipeline_shape()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["REGION"]),
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "REGION";
                    shape.Fn = AggregateFn.Count;
                }),
            ])));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline"
            && e.Message.Contains("unsupported pipeline shape"));
    }

    [Fact]
    public void Empty_pipeline_and_null_pipeline_mean_the_bare_source_stage()
    {
        Assert.Equal(ViewMode.Grid, Validate(new ReportState()).View.Mode);
        Assert.Equal(ViewMode.Grid, Validate(new ReportState { Pipeline = [] }).View.Mode);
    }

    // ---- the shelf is server-inert ----

    [Fact]
    public void Shelf_entries_are_never_validated()
    {
        var result = Validate(Doc(
            source: new StageLayer { Columns = ["CUSTOMER"] },
            shelf: new()
            {
                ["groupBy"] =
                [
                    Group(by: ["NO_SUCH_COLUMN"], values: [Metric("banana", "GHOST", AggregateFn.Sum)]),
                ],
                ["chart"] =
                [
                    ChartStage(shape =>
                    {
                        shape.Type = "donut";
                        shape.Label = "MISSING";
                    }),
                ],
                ["garbage"] =
                [
                    new PipelineStage { Shape = new StageShape { Kind = "teleport" } },
                ],
            }));

        Assert.Equal(ViewMode.Grid, result.View.Mode);
        Assert.Empty(result.Ignored);
    }

    // ---- group tail ----

    [Fact]
    public void Group_stage_validates_its_own_settings_and_silently_leaves_source_settings_inactive()
    {
        var result = Validate(Doc(
            source: new StageLayer
            {
                Breaks = ["STATUS"],
                Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
                Sorts = [new SortRule { Col = "REGION", Dir = SortDir.Desc }],
            },
            tail:
            [
                Group(by: ["REGION", "GHOST"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)]),
            ]));

        Assert.Equal(ViewMode.GroupBy, result.View.Mode);
        Assert.Equal("REGION", Assert.Single(result.View.GroupBy).Name);
        var metric = Assert.Single(result.View.Values);
        Assert.Equal(("m1", "AMOUNT", AggregateFn.Sum), (metric.Id, metric.Column.Name, metric.Fn));
        Assert.Empty(result.Breaks);
        Assert.Empty(result.Aggregates);
        Assert.Empty(result.Sorts);                          // source sorts are inactive, not errors
        Assert.Contains(result.Ignored, i => i.Detail.Contains("unknown group column 'GHOST'"));
        Assert.DoesNotContain(result.Ignored, i => i.Detail.Contains("control breaks"));
    }

    [Fact]
    public void Group_stage_requires_a_valid_dimension()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail: [Group(by: ["GHOST"])])));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.by"
            && e.Message.Contains("at least one valid group column"));
    }

    [Fact]
    public void Grouping_by_a_column_named___count_is_reserved()
    {
        var schemaWithCount = OrdersSchema.Append(Col("__count", typeof(long))).ToList();
        var ex = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                Doc(tail: [Group(by: ["__count"])]),
                schemaWithCount));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.by"
            && e.Message.Contains("'__count' is reserved"));
    }

    [Fact]
    public void Metric_ids_are_validated_like_computed_ids()
    {
        var badId = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail: [Group(by: ["REGION"], values: [Metric("v0", "AMOUNT", AggregateFn.Sum)])])));
        Assert.Contains(badId.Errors, e =>
            e.Path == "pipeline[1].shape.values[0]" && e.Message.Contains("must match m1"));

        var dupe = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["REGION"], values:
                [
                    Metric("m1", "AMOUNT", AggregateFn.Sum),
                    Metric("m1", "AMOUNT", AggregateFn.Avg),
                ]),
            ])));
        Assert.Contains(dupe.Errors, e =>
            e.Path == "pipeline[1].shape.values[1]" && e.Message.Contains("duplicate metric id"));

        var schemaWithM1 = OrdersSchema.Append(Col("M1", typeof(string))).ToList();
        var shadow = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                Doc(tail: [Group(by: ["REGION"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)])]),
                schemaWithM1));
        Assert.Contains(shadow.Errors, e =>
            e.Path == "pipeline[1].shape.values[0]" && e.Message.Contains("shadows a schema column"));

        var incompatible = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail: [Group(by: ["REGION"], values: [Metric("m1", "CUSTOMER", AggregateFn.Sum)])])));
        Assert.Contains(incompatible.Errors, e =>
            e.Path == "pipeline[1].shape.values[0]" && e.Message.Contains("not valid for text column 'CUSTOMER'"));
    }

    [Fact]
    public void Unknown_metric_column_is_ignored_not_an_error()
    {
        var result = Validate(Doc(tail:
        [
            Group(by: ["REGION"], values:
            [
                Metric("m1", "GHOST", AggregateFn.Sum),
                Metric("m2", "AMOUNT", AggregateFn.Sum),
            ]),
        ]));

        var metric = Assert.Single(result.View.Values);
        Assert.Equal("m2", metric.Id);
        Assert.Contains(result.Ignored, i => i.Kind == "metric" && i.Detail.Contains("GHOST"));
    }

    [Fact]
    public void Group_layer_binds_to_the_stage_schema_not_the_source_schema()
    {
        // Sorting by a metric is legal; computed columns derive from dims, __count,
        // and metrics; a source column that is not a dim does not exist here.
        var result = Validate(Doc(tail:
        [
            Group(
                by: ["REGION"],
                values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Computed = [new ComputedColumn { Id = "c2", Expr = "ROUND(m1 / __count, 2)" }],
                    Sorts = [new SortRule { Col = "m1", Dir = SortDir.Desc }],
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "h1", Scope = "cell", Col = "c2", Expr = "c2 > 100",
                            Style = new HighlightStyle { Bg = "gold" },
                        },
                    ],
                }),
        ]));

        var layer = result.View.GroupLayer!;
        Assert.Equal("c2", Assert.Single(layer.Computed).Effect.Column.Name);
        Assert.Equal("m1", Assert.Single(layer.Sorts).Column.Name);
        Assert.Equal("c2", Assert.Single(layer.Decorations).Effect.Column!.Name);
        Assert.Equal(["REGION", "__count", "m1", "c2"], layer.SelectColumns.Select(c => c.Name));

        var unknown = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(
                    by: ["REGION"],
                    layer: new StageLayer
                    {
                        Computed = [new ComputedColumn { Id = "c2", Expr = "AMOUNT * 2" }],
                    }),
            ])));
        Assert.Contains(unknown.Errors, e =>
            e.Path == "pipeline[1].layer.computed[0].expr" && e.Message.Contains("AMOUNT"));
    }

    [Fact]
    public void Group_layer_rejects_source_only_slots()
    {
        var filters = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["REGION"], layer: new StageLayer { Filters = [Filter("1 = 1")] }),
            ])));
        Assert.Contains(filters.Errors, e =>
            e.Path == "pipeline[1].layer.filters" && e.Message.Contains("filters live on the source stage"));

        var breaks = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["REGION"], layer: new StageLayer { Breaks = ["REGION"] }),
            ])));
        Assert.Contains(breaks.Errors, e =>
            e.Path == "pipeline[1].layer.breaks" && e.Message.Contains("source stage"));

        var aggregates = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["REGION"], layer: new StageLayer
                {
                    Aggregates = [new AggregateRule { Col = "REGION", Fn = AggregateFn.Count }],
                }),
            ])));
        Assert.Contains(aggregates.Errors, e =>
            e.Path == "pipeline[1].layer.aggregates" && e.Message.Contains("source stage"));
    }

    // ---- spread tail ----

    [Fact]
    public void Spread_splits_group_dimensions_into_rows_and_cols()
    {
        var result = Validate(Doc(tail:
        [
            Group(by: ["CUSTOMER", "STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)]),
            Spread(cols: ["STATUS"], totals: true),
        ]));

        Assert.Equal(ViewMode.Pivot, result.View.Mode);
        Assert.Equal("CUSTOMER", Assert.Single(result.View.PivotRows).Name);
        Assert.Equal("STATUS", Assert.Single(result.View.PivotCols).Name);
        Assert.True(result.View.Totals);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public void Spread_col_not_in_group_by_goes_to_ignored()
    {
        var result = Validate(Doc(tail:
        [
            Group(by: ["CUSTOMER", "STATUS"]),
            Spread(cols: ["STATUS", "REGION"]),
        ]));

        Assert.Equal("STATUS", Assert.Single(result.View.PivotCols).Name);
        Assert.Contains(result.Ignored, i => i.Kind == "spread"
            && i.Detail == "spread column 'REGION' is not a group column of the preceding stage");
    }

    [Fact]
    public void Spread_requires_a_valid_column_dimension_and_a_leftover_row_dimension()
    {
        var noCols = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["CUSTOMER"]),
                Spread(cols: ["REGION"]),   // not a group column → ignored → none left
            ])));
        Assert.Contains(noCols.Errors, e => e.Path == "pipeline[2].shape.cols"
            && e.Message.Contains("at least one valid column dimension"));

        var noRows = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["STATUS"]),
                Spread(cols: ["STATUS"]),
            ])));
        Assert.Contains(noRows.Errors, e => e.Path == "pipeline[2].shape.cols"
            && e.Message.Contains("left over as a row dimension"));
    }

    [Fact]
    public void Spread_layer_carries_labels_and_formats_only()
    {
        var computed = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["CUSTOMER", "STATUS"]),
                Spread(cols: ["STATUS"], layer: new StageLayer
                {
                    Computed = [new ComputedColumn { Id = "c1", Expr = "1" }],
                }),
            ])));
        Assert.Contains(computed.Errors, e => e.Path == "pipeline[2].layer.computed");

        var sorts = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["CUSTOMER", "STATUS"]),
                Spread(cols: ["STATUS"], layer: new StageLayer
                {
                    Sorts = [new SortRule { Col = "CUSTOMER" }],
                }),
            ])));
        Assert.Contains(sorts.Errors, e => e.Path == "pipeline[2].layer.sorts");

        var highlights = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["CUSTOMER", "STATUS"]),
                Spread(cols: ["STATUS"], layer: new StageLayer
                {
                    Highlights = [new HighlightRule { Id = "h1", Expr = "1 = 1", Style = new HighlightStyle { Bg = "red" } }],
                }),
            ])));
        Assert.Contains(highlights.Errors, e => e.Path == "pipeline[2].layer.highlights");

        var columns = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                Group(by: ["CUSTOMER", "STATUS"]),
                Spread(cols: ["STATUS"], layer: new StageLayer { Columns = ["CUSTOMER"] }),
            ])));
        Assert.Contains(columns.Errors, e => e.Path == "pipeline[2].layer.columns");

        // Labels and formats — presentation, never validated — pass untouched.
        var ok = Validate(Doc(tail:
        [
            Group(by: ["CUSTOMER", "STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)]),
            Spread(cols: ["STATUS"], layer: new StageLayer
            {
                Labels = new() { ["m1@[\"SHIPPED\"]"] = "Shipped Total", ["GHOST"] = "Dormant" },
                Formats = new() { ["m1@[\"SHIPPED\"]"] = new ColumnFormat { Mask = "decimal2" } },
            }),
        ]));
        Assert.Equal("Shipped Total", ok.View.SpreadLabels!["m1@[\"SHIPPED\"]"]);
    }

    [Fact]
    public void Under_a_spread_group_highlights_and_metric_sorts_become_notices()
    {
        var result = Validate(Doc(tail:
        [
            Group(
                by: ["CUSTOMER", "STATUS"],
                values: [Metric("m1", "AMOUNT", AggregateFn.Sum)],
                layer: new StageLayer
                {
                    Sorts =
                    [
                        new SortRule { Col = "CUSTOMER", Dir = SortDir.Desc },   // row dim: honored
                        new SortRule { Col = "m1", Dir = SortDir.Desc },         // metric: inert
                    ],
                    Highlights =
                    [
                        new HighlightRule
                        {
                            Id = "h1", Scope = "row", Expr = "m1 > 1",
                            Style = new HighlightStyle { Bg = "red" },
                        },
                    ],
                }),
            Spread(cols: ["STATUS"]),
        ]));

        var layer = result.View.GroupLayer!;
        Assert.Equal("CUSTOMER", Assert.Single(layer.Sorts).Column.Name);
        Assert.Empty(layer.Decorations);
        Assert.Contains(result.Ignored, i => i.Kind == "sort"
            && i.Detail.Contains("group sort on 'm1' is inert under a spread"));
        Assert.Contains(result.Ignored, i => i.Kind == "highlight"
            && i.Detail.Contains("group-stage highlights are inert under a spread"));
    }

    [Fact]
    public void Inactive_source_rules_and_shelf_definitions_are_not_validated_under_a_pivot()
    {
        var result = Validate(Doc(
            source: new StageLayer
            {
                Columns = ["CUSTOMER", "REMOVED_GRID_COLUMN"],
                Sorts = [new SortRule { Col = "REMOVED_GRID_COLUMN" }],
                Breaks = ["REMOVED_GRID_COLUMN"],
                Aggregates = [new AggregateRule { Col = "REMOVED_GRID_COLUMN", Fn = AggregateFn.Sum }],
                Highlights =
                [
                    new HighlightRule
                    {
                        Id = "h1", Scope = "cell", Col = "REMOVED_GRID_COLUMN",
                        Expr = "REMOVED_GRID_COLUMN > 1", Style = new HighlightStyle { Bg = "red" },
                    },
                ],
            },
            tail:
            [
                Group(by: ["CUSTOMER", "STATUS"], values: [Metric("m1", "AMOUNT", AggregateFn.Sum)]),
                Spread(cols: ["STATUS"]),
            ],
            shelf: new()
            {
                ["chart"] =
                [
                    ChartStage(shape =>
                    {
                        shape.Type = "donut";
                        shape.Label = "MISSING";
                        shape.Value = "GHOST";
                    }),
                ],
            }));

        Assert.Equal(ViewMode.Pivot, result.View.Mode);
        Assert.Empty(result.Sorts);
        Assert.Empty(result.Breaks);
        Assert.Empty(result.Aggregates);
        Assert.Empty(result.Rules.Decorations);
        Assert.Empty(result.Ignored);
    }

    // ---- chart tail ----

    [Fact]
    public void Chart_stage_validates_its_own_settings_and_silently_leaves_source_settings_inactive()
    {
        var result = Validate(Doc(
            source: new StageLayer
            {
                Breaks = ["REGION"],
                Aggregates = [new AggregateRule { Col = "AMOUNT", Fn = AggregateFn.Avg }],
                Sorts = [new SortRule { Col = "AMOUNT", Dir = SortDir.Desc }],
            },
            tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Value = "AMOUNT";
                    shape.Fn = AggregateFn.Sum;
                    shape.Orientation = "horizontal";
                    shape.Sort = new ChartSortSpec { By = "value", Dir = SortDir.Desc };
                    shape.LabelAxisTitle = "  Status  ";
                    shape.ValueAxisTitle = "Total";
                }),
            ]));

        Assert.Equal(ViewMode.Chart, result.View.Mode);
        var chart = result.View.Chart!;
        Assert.Equal(ChartType.Bar, chart.Type);
        Assert.Equal("STATUS", chart.Label.Name);
        Assert.Equal("AMOUNT", chart.Value!.Name);
        Assert.Equal(AggregateFn.Sum, chart.Fn);
        Assert.Equal(ChartOrientation.Horizontal, chart.Orientation);
        Assert.Equal((ChartSortBy.Value, SortDir.Desc), (chart.SortBy, chart.SortDir));
        Assert.Equal("Status", chart.LabelAxisTitle);
        Assert.Equal("Total", chart.ValueAxisTitle);
        Assert.Empty(result.Breaks);
        Assert.Empty(result.Aggregates);
        Assert.Empty(result.Sorts);
        Assert.DoesNotContain(result.Ignored, i => i.Kind == "view");
    }

    [Fact]
    public void Chart_defaults_fill_optional_fields()
    {
        var result = Validate(Doc(tail:
        [
            ChartStage(shape =>
            {
                shape.Type = "pie";
                shape.Label = "STATUS";
                shape.Fn = AggregateFn.Count;
            }),
        ]));

        var chart = result.View.Chart!;
        Assert.Null(chart.Value);                                    // count alone = COUNT(*)
        Assert.Equal(AggregateFn.Count, chart.Fn);
        Assert.Equal(ChartOrientation.Vertical, chart.Orientation);
        Assert.Equal((ChartSortBy.Label, SortDir.Asc), (chart.SortBy, chart.SortDir));
        Assert.Null(chart.LabelAxisTitle);
        Assert.Null(chart.ValueAxisTitle);
    }

    [Fact]
    public void Chart_metric_must_be_numeric_where_grid_aggregation_is_looser()
    {
        // max(ORDER_DATE) is a valid grid aggregate but produces a date — unplottable.
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Value = "ORDER_DATE";
                    shape.Fn = AggregateFn.Max;
                }),
            ])));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.value" && e.Message.Contains("numeric"));
    }

    [Fact]
    public void Chart_without_fn_requires_a_number_value_column()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "line";
                    shape.Label = "ORDER_DATE";
                    shape.Value = "CUSTOMER";
                }),
            ])));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.value" && e.Message.Contains("number"));
    }

    [Fact]
    public void Chart_value_is_required_unless_counting_rows()
    {
        var sum = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Sum;
                }),
            ])));
        Assert.Contains(sum.Errors, e => e.Path == "pipeline[1].shape.value" && e.Message.Contains("'sum'"));

        var distinct = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.CountDistinct;
                }),
            ])));
        Assert.Contains(distinct.Errors, e => e.Path == "pipeline[1].shape.value");

        var bare = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                }),
            ])));
        Assert.Contains(bare.Errors, e => e.Path == "pipeline[1].shape.value");
    }

    [Fact]
    public void Chart_structural_problems_are_errors()
    {
        var badType = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "donut";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Count;
                }),
            ])));
        Assert.Contains(badType.Errors, e => e.Path == "pipeline[1].shape.type" && e.Message.Contains("donut"));

        var noLabel = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Fn = AggregateFn.Count;
                }),
            ])));
        Assert.Contains(noLabel.Errors, e => e.Path == "pipeline[1].shape.label");

        var unknownLabel = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "GHOST";
                    shape.Fn = AggregateFn.Count;
                }),
            ])));
        Assert.Contains(unknownLabel.Errors, e => e.Path == "pipeline[1].shape.label" && e.Message.Contains("GHOST"));

        var badOrientation = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Count;
                    shape.Orientation = "diagonal";
                }),
            ])));
        Assert.Contains(badOrientation.Errors, e => e.Path == "pipeline[1].shape.orientation");

        var badSort = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(tail:
            [
                ChartStage(shape =>
                {
                    shape.Type = "bar";
                    shape.Label = "STATUS";
                    shape.Fn = AggregateFn.Count;
                    shape.Sort = new ChartSortSpec { By = "hue" };
                }),
            ])));
        Assert.Contains(badSort.Errors, e => e.Path == "pipeline[1].shape.sort.by");
    }

    [Fact]
    public void Chart_label_of_unknowable_kind_is_rejected()
    {
        var schemaWithBlob = OrdersSchema.Append(Col("PAYLOAD", typeof(byte[]))).ToList();
        var ex = Assert.Throws<ReportValidationException>(() =>
            StateValidator.Validate(
                OrdersDefinition(ReportDialect.Sqlite),
                Doc(tail:
                [
                    ChartStage(shape =>
                    {
                        shape.Type = "bar";
                        shape.Label = "PAYLOAD";
                        shape.Fn = AggregateFn.Count;
                    }),
                ]),
                schemaWithBlob));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.label" && e.Message.Contains("cannot label"));
    }

    // ---- definition edit link (hidden template-column projection) ----

    private static ReportDefinition EditLinkDefinition(string template)
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.EditLink = new ReportEditLink { UrlTemplate = template };
        return def;
    }

    [Fact]
    public void Edit_link_template_columns_join_the_projection_but_not_display_metadata()
    {
        var result = Validate(
            Doc(source: new StageLayer { Columns = ["CUSTOMER"] }),
            EditLinkDefinition("/orders/{order_id}/edit?r={REGION}"));

        Assert.Equal(["CUSTOMER"], result.SelectColumns.Select(c => c.Name));
        // Case-insensitive binding resolves to canonical casing.
        Assert.Equal(["CUSTOMER", "ORDER_ID", "REGION"], result.ProjectionColumns.Select(c => c.Name));
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public void Edit_link_columns_already_displayed_are_not_duplicated()
    {
        var result = Validate(
            Doc(source: new StageLayer { Columns = ["ORDER_ID", "CUSTOMER"] }),
            EditLinkDefinition("/orders/{ORDER_ID}/edit"));

        Assert.Equal(["ORDER_ID", "CUSTOMER"], result.ProjectionColumns.Select(c => c.Name));
    }

    [Fact]
    public void Edit_link_with_unknown_placeholder_degrades_into_ignored()
    {
        var result = Validate(
            Doc(source: new StageLayer { Columns = ["CUSTOMER"] }),
            EditLinkDefinition("/orders/{GHOST}/edit"));

        Assert.Equal(["CUSTOMER"], result.ProjectionColumns.Select(c => c.Name));
        var item = Assert.Single(result.Ignored);
        Assert.Equal("editLink", item.Kind);
        Assert.Contains("GHOST", item.Detail);
    }

    [Fact]
    public void Edit_link_binding_only_runs_in_grid_mode()
    {
        // An unresolvable placeholder proves it: grid mode reports it through
        // ignored[], a group tail never even binds the template.
        var grouped = Validate(
            Doc(tail: [Group(by: ["REGION"])]),
            EditLinkDefinition("/orders/{GHOST}/edit"));

        Assert.Equal(ViewMode.GroupBy, grouped.View.Mode);
        Assert.DoesNotContain(grouped.Ignored, i => i.Kind == "editLink");
    }

    // ---- per-column overrides (sort/filter/break enforcement) ----

    private static ReportDefinition RestrictedDefinition(
        string column,
        bool? sortable = null,
        bool? filterable = null)
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.Columns = new()
        {
            [column] = new ReportColumnOverride { Sortable = sortable, Filterable = filterable },
        };
        return def;
    }

    [Fact]
    public void Sorts_on_a_non_sortable_column_are_stripped_into_ignored()
    {
        var result = Validate(
            Doc(source: new StageLayer
            {
                Sorts = [new SortRule { Col = "notes", Dir = SortDir.Desc }, new SortRule { Col = "CUSTOMER" }],
            }),
            RestrictedDefinition("NOTES", sortable: false));

        var sort = Assert.Single(result.Sorts);
        Assert.Equal("CUSTOMER", sort.Column.Name);
        var item = Assert.Single(result.Ignored);
        Assert.Equal("sort", item.Kind);
        Assert.Equal("column 'NOTES' is not sortable", item.Detail);
    }

    [Fact]
    public void Group_layer_sorts_on_a_non_sortable_dim_are_stripped_too()
    {
        var result = Validate(
            Doc(tail:
            [
                Group(
                    by: ["REGION"],
                    layer: new StageLayer { Sorts = [new SortRule { Col = "REGION" }] }),
            ]),
            RestrictedDefinition("REGION", sortable: false));

        // Grouping by the column stays allowed — only its ordering is withdrawn.
        Assert.Equal(ViewMode.GroupBy, result.View.Mode);
        Assert.Empty(result.View.GroupLayer!.Sorts);
        Assert.Contains(result.Ignored, i => i.Kind == "sort" && i.Detail.Contains("REGION"));
    }

    [Fact]
    public void Breaks_on_a_non_sortable_column_are_stripped_and_stop_forcing_selection()
    {
        var result = Validate(
            Doc(source: new StageLayer { Columns = ["CUSTOMER"], Breaks = ["NOTES"] }),
            RestrictedDefinition("NOTES", sortable: false));

        Assert.Empty(result.Breaks);
        Assert.Equal(["CUSTOMER"], result.SelectColumns.Select(c => c.Name));
        var item = Assert.Single(result.Ignored);
        Assert.Equal("break", item.Kind);
        Assert.Contains("control breaks imply sorting", item.Detail);
    }

    [Fact]
    public void Filters_referencing_a_non_filterable_column_are_dropped_whole()
    {
        var result = Validate(
            Doc(source: new StageLayer
            {
                Filters = [Filter("AMOUNT > 100 AND STATUS = 'X'"), Filter("STATUS = 'SHIPPED'")],
            }),
            RestrictedDefinition("AMOUNT", filterable: false));

        var kept = Assert.Single(result.Rules.RowPredicates);
        Assert.NotNull(kept.Expression.Ast);
        var item = Assert.Single(result.Ignored);
        Assert.Equal("filter", item.Kind);
        Assert.Equal("filter references non-filterable column 'AMOUNT'", item.Detail);
    }

    [Fact]
    public void Computed_columns_stay_sortable_and_filterable_despite_restricted_inputs()
    {
        var def = RestrictedDefinition("AMOUNT", sortable: false, filterable: false);
        var result = Validate(
            Doc(source: new StageLayer
            {
                Computed = [new ComputedColumn { Id = "c1", Expr = "AMOUNT * 2" }],
                Filters = [Filter("c1 > 100")],
                Sorts = [new SortRule { Col = "c1" }],
            }),
            def);

        Assert.Single(result.Rules.RowPredicates);
        Assert.Equal("c1", Assert.Single(result.Sorts).Column.Name);
        Assert.Empty(result.Ignored);
    }

    [Fact]
    public void Malformed_filters_still_error_before_restriction_stripping()
    {
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(
                Doc(source: new StageLayer { Filters = [Filter("NO_SUCH = 1")] }),
                RestrictedDefinition("AMOUNT", filterable: false)));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.filters[0].expr");
    }

    [Fact]
    public void Definition_labels_merge_column_overrides_over_column_labels()
    {
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.ColumnLabels = new() { ["AMOUNT"] = "Total" };
        def.Columns = new() { ["ORDER_ID"] = new ReportColumnOverride { Label = "Order #" } };

        var result = Validate(Doc(), def);

        Assert.Equal("Total", result.Labels["AMOUNT"]);
        Assert.Equal("Order #", result.Labels["ORDER_ID"]);
    }

    // ---- structural nulls: precise 400s, never NullReferenceException 500s ----

    [Fact]
    public void Null_pipeline_stages_and_list_elements_are_precise_errors()
    {
        var state = new ReportState
        {
            Pipeline =
            [
                new PipelineStage
                {
                    Shape = new StageShape { Kind = "source" },
                    Layer = new StageLayer { Sorts = [null!] },
                },
                null!,
            ],
        };

        var ex = Assert.Throws<ReportValidationException>(() => Validate(state));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1]" && e.Message.Contains("null"));
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.sorts[0]");
    }

    [Fact]
    public void Null_identifier_and_expression_properties_are_precise_errors()
    {
        var state = Doc(
            source: new StageLayer
            {
                Columns = [null!],
                Filters = [new FilterRule { Expr = null! }],
                Sorts = [new SortRule { Col = null! }],
                Aggregates = [new AggregateRule { Col = null! }],
            },
            tail: [Group(["CUSTOMER"], [new MetricRule { Id = null!, Col = null!, Fn = AggregateFn.Sum }])]);

        var ex = Assert.Throws<ReportValidationException>(() => Validate(state));

        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.columns[0]");
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.filters[0].expr");
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.sorts[0].col");
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[0].layer.aggregates[0].col");
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.values[0].id");
        Assert.Contains(ex.Errors, e => e.Path == "pipeline[1].shape.values[0].col");
    }

    [Fact]
    public void Null_stage_in_the_shelf_is_a_precise_error()
    {
        // Shelf tails are inert configuration, but the resolver still deep-copies
        // them — a null stage crashed that copy before this gate existed.
        var ex = Assert.Throws<ReportValidationException>(() =>
            Validate(Doc(shelf: new() { ["chart"] = [null!] })));

        Assert.Contains(ex.Errors, e => e.Path == "shelf.chart[0]");
    }

    [Fact]
    public void Structurally_broken_default_state_fails_as_a_configuration_error()
    {
        // Server-side data, not the caller's document: blaming the request with a
        // 400 would be wrong, so this surfaces as the sanitized config failure.
        var def = OrdersDefinition(ReportDialect.Sqlite);
        def.DefaultState = new ReportState { Pipeline = [null!] };

        var ex = Assert.Throws<InvalidOperationException>(() => Validate(new ReportState(), def));

        Assert.Contains("default state document is structurally invalid", ex.Message);
        Assert.Contains("pipeline[0]", ex.Message);
    }
}

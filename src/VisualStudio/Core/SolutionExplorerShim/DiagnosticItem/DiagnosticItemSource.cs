// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.SolutionExplorer
{
    internal partial class DiagnosticItemSource : IAttachedCollectionSource
    {
        private static readonly DiagnosticDescriptorComparer s_comparer = new DiagnosticDescriptorComparer();

        private AnalyzerItem _item;
        private BulkObservableCollection<DiagnosticItem> _diagnosticItems;
        private Workspace _workspace;
        private ProjectId _projectId;
        private ReportDiagnostic _generalDiagnosticOption;
        private ImmutableDictionary<string, ReportDiagnostic> _specificDiagnosticOptions;

        public DiagnosticItemSource(AnalyzerItem item)
        {
            _item = item;
        }

        public object SourceItem
        {
            get
            {
                return _item;
            }
        }

        public bool HasItems
        {
            get
            {
                _workspace = _item.AnalyzersFolder.Workspace;
                _projectId = _item.AnalyzersFolder.ProjectId;

                var project = _workspace.CurrentSolution.GetProject(_projectId);
                return _item.AnalyzerReference.GetAnalyzers(project.Language).Length > 0;
            }
        }

        public IEnumerable Items
        {
            get
            {
                if (_diagnosticItems == null)
                {
                    _workspace = _item.AnalyzersFolder.Workspace;
                    _projectId = _item.AnalyzersFolder.ProjectId;

                    var project = _workspace.CurrentSolution.GetProject(_projectId);
                    _generalDiagnosticOption = project.CompilationOptions.GeneralDiagnosticOption;
                    _specificDiagnosticOptions = project.CompilationOptions.SpecificDiagnosticOptions;

                    _diagnosticItems = new BulkObservableCollection<DiagnosticItem>();
                    _diagnosticItems.AddRange(GetDiagnosticItems(project.Language));

                    _workspace.WorkspaceChanged += Workspace_WorkspaceChanged;
                }

                Logger.Log(
                    FunctionId.SolutionExplorer_DiagnosticItemSource_GetItems,
                    KeyValueLogMessage.Create(m => m["Count"] = _diagnosticItems.Count));

                return _diagnosticItems;
            }
        }

        private IEnumerable<DiagnosticItem> GetDiagnosticItems(string language)
        {
            // Within an analyzer assembly, an individual analyzer may report multiple different diagnostics
            // with the same ID. Or, multiple analyzers may report diagnostics with the same ID. Or a
            // combination of the two may occur.
            // We only want to show one node in Solution Explorer for a given ID. So we pick one, but we need
            // to be consistent in which one we pick. Diagnostics with the same ID may have different
            // descriptions or messages, and it would be strange if the node's name changed from one run of
            // VS to another. So we group the diagnostics by ID, sort them within a group, and take the first
            // one.

            return _item.AnalyzerReference.GetAnalyzers(language)
                .SelectMany(a => a.SupportedDiagnostics)
                .GroupBy(d => d.Id)
                .OrderBy(g => g.Key, StringComparer.CurrentCulture)
                .Select(g =>
                {
                    var selectedDiagnostic = g.OrderBy(d => d, s_comparer).First();
                    return new DiagnosticItem(_item, selectedDiagnostic, GetEffectiveSeverity(selectedDiagnostic.Id, _specificDiagnosticOptions, _generalDiagnosticOption, selectedDiagnostic.DefaultSeverity, selectedDiagnostic.IsEnabledByDefault));
                });
        }

        private void Workspace_WorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            if (e.Kind == WorkspaceChangeKind.SolutionCleared ||
                e.Kind == WorkspaceChangeKind.SolutionReloaded ||
                e.Kind == WorkspaceChangeKind.SolutionRemoved)
            {
                _workspace.WorkspaceChanged -= Workspace_WorkspaceChanged;
            }
            else if (e.ProjectId == _projectId)
            {
                if (e.Kind == WorkspaceChangeKind.ProjectRemoved)
                {
                    _workspace.WorkspaceChanged -= Workspace_WorkspaceChanged;
                }
                else if (e.Kind == WorkspaceChangeKind.ProjectChanged)
                {
                    var project = e.NewSolution.GetProject(_projectId);
                    var newGeneralDiagnosticOption = project.CompilationOptions.GeneralDiagnosticOption;
                    var newSpecificDiagnosticOptions = project.CompilationOptions.SpecificDiagnosticOptions;

                    if (newGeneralDiagnosticOption != _generalDiagnosticOption ||
                        !object.ReferenceEquals(newSpecificDiagnosticOptions, _specificDiagnosticOptions))
                    {
                        _generalDiagnosticOption = newGeneralDiagnosticOption;
                        _specificDiagnosticOptions = newSpecificDiagnosticOptions;

                        foreach (var item in _diagnosticItems)
                        {
                            item.UpdateEffectiveSeverity(GetEffectiveSeverity(item.Descriptor.Id, _specificDiagnosticOptions, _generalDiagnosticOption, item.Descriptor.DefaultSeverity, item.Descriptor.IsEnabledByDefault));
                        }
                    }
                }
            }
        }

        // If you update this method, also consider making updates to CSharpDiagnosticFilter.GetDiagnosticReport and
        // VisualBasicDiagnosticFilter.GetDiagnosticReport.
        internal static ReportDiagnostic GetEffectiveSeverity(string ruleId, IDictionary<string, ReportDiagnostic> specificOptions, ReportDiagnostic generalOption, DiagnosticSeverity defaultSeverity, bool enabledByDefault)
        {
            ReportDiagnostic report = ReportDiagnostic.Default;
            var isSpecified = specificOptions.TryGetValue(ruleId, out report);
            if (!isSpecified)
            {
                report = enabledByDefault ? ReportDiagnostic.Default : ReportDiagnostic.Suppress;
            }

            if (report == ReportDiagnostic.Default)
            {
                switch (generalOption)
                {
                    case ReportDiagnostic.Error:
                        if (defaultSeverity == DiagnosticSeverity.Warning)
                        {
                            if (!isSpecified)
                            {
                                return ReportDiagnostic.Error;
                            }
                        }

                        break;
                    case ReportDiagnostic.Suppress:
                        if (defaultSeverity == DiagnosticSeverity.Warning || defaultSeverity == DiagnosticSeverity.Info)
                        {
                            return ReportDiagnostic.Suppress;
                        }

                        break;
                    default:
                        break;
                }

                return MapSeverityToReport(defaultSeverity);
            }

            return report;
        }

        private static ReportDiagnostic MapSeverityToReport(DiagnosticSeverity defaultSeverity)
        {
            switch (defaultSeverity)
            {
                case DiagnosticSeverity.Hidden:
                    return ReportDiagnostic.Hidden;
                case DiagnosticSeverity.Info:
                    return ReportDiagnostic.Info;
                case DiagnosticSeverity.Warning:
                    return ReportDiagnostic.Warn;
                case DiagnosticSeverity.Error:
                    return ReportDiagnostic.Error;
                default:
                    throw new ArgumentException("Unhandled DiagnosticSeverity: " + defaultSeverity, "defaultSeverity");
            }
        }
    }
}

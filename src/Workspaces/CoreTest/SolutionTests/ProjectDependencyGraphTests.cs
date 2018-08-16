﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.UnitTests;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Host.UnitTests
{
    [UseExportProvider]
    public class ProjectDependencyGraphTests : TestBase
    {
        #region GetTopologicallySortedProjects

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void ProjectDependencyGraph_GetTopologicallySortedProjects()
        {
            VerifyTopologicalSort("A", "A");
            VerifyTopologicalSort("A B", "AB", "BA");
            VerifyTopologicalSort("C:A,B B:A A", "ABC");
            VerifyTopologicalSort("B:A A C:A D:C,B", "ABCD", "ACBD");
        }

        private void VerifyTopologicalSort(string projectReferences, params string[] expectedResults)
        {
            Solution solution = CreateSolutionFromReferenceMap(projectReferences);
            var projectDependencyGraph = solution.GetProjectDependencyGraph();
            var projectIds = projectDependencyGraph.GetTopologicallySortedProjects(CancellationToken.None);

            var actualResult = string.Concat(projectIds.Select(id => solution.GetProject(id).AssemblyName));
            Assert.Contains<string>(actualResult, expectedResults);
        }

        #endregion

        #region Dependency Sets

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(542438, "http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542438")]
        public void ProjectDependencyGraph_GetDependencySets()
        {
            VerifyDependencySets("A B:A C:A D E:D F:D", "ABC DEF");
            VerifyDependencySets("A B:A,C C", "ABC");
            VerifyDependencySets("A B", "A B");
            VerifyDependencySets("A B C:B", "A BC");
            VerifyDependencySets("A B:A C:A D:B,C", "ABCD");
        }

        private void VerifyDependencySets(string projectReferences, string expectedResult)
        {
            Solution solution = CreateSolutionFromReferenceMap(projectReferences);
            var projectDependencyGraph = solution.GetProjectDependencyGraph();
            var projectIds = projectDependencyGraph.GetDependencySets(CancellationToken.None);
            var actualResult = string.Join(" ",
                projectIds.Select(
                    group => string.Concat(
                        group.Select(p => solution.GetProject(p).AssemblyName).OrderBy(n => n))).OrderBy(n => n));
            Assert.Equal(expectedResult, actualResult);
        }

        #endregion

        #region GetProjectsThatThisProjectTransitivelyDependsOn

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void ProjectDependencyGraph_GetProjectsThatThisProjectTransitivelyDependsOn()
        {
            VerifyTransitiveReferences(CreateSolutionFromReferenceMap("A"), "A", new string[] { });
            VerifyTransitiveReferences(CreateSolutionFromReferenceMap("B:A A"), "B", new string[] { "A" });
            VerifyTransitiveReferences(CreateSolutionFromReferenceMap("C:B B:A A"), "C", new string[] { "B", "A" });
            VerifyTransitiveReferences(CreateSolutionFromReferenceMap("C:B B:A A"), "A", new string[] { });
        }

        private void VerifyTransitiveReferences(Solution solution, string project, string[] expectedResults)
        {
            var projectDependencyGraph = solution.GetProjectDependencyGraph();
            var projectId = solution.GetProjectsByName(project).Single().Id;
            var projectIds = projectDependencyGraph.GetProjectsThatThisProjectTransitivelyDependsOn(projectId);

            var actualResults = projectIds.Select(id => solution.GetProject(id).Name);

            Assert.Equal<string>(
                expectedResults.OrderBy(n => n),
                actualResults.OrderBy(n => n));
        }

        #endregion

        #region GetProjectsThatTransitivelyDependOnThisProject

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void ProjectDependencyGraph_GetProjectsThatTransitivelyDependOnThisProject()
        {
            VerifyReverseTransitiveReferences(CreateSolutionFromReferenceMap("A"), "A", new string[] { });
            VerifyReverseTransitiveReferences(CreateSolutionFromReferenceMap("B:A A"), "A", new string[] { "B" });
            VerifyReverseTransitiveReferences(CreateSolutionFromReferenceMap("C:B B:A A"), "A", new string[] { "B", "C" });
            VerifyReverseTransitiveReferences(CreateSolutionFromReferenceMap("C:B B:A A"), "C", new string[] { });
            VerifyReverseTransitiveReferences(CreateSolutionFromReferenceMap("D:C,B B:A C A"), "A", new string[] { "D", "B" });
        }

        private void VerifyReverseTransitiveReferences(Solution solution, string project, string[] expectedResults)
        {
            var projectDependencyGraph = solution.GetProjectDependencyGraph();
            var projectId = solution.GetProjectsByName(project).Single().Id;
            var projectIds = projectDependencyGraph.GetProjectsThatTransitivelyDependOnThisProject(projectId);

            var actualResults = projectIds.Select(id => solution.GetProject(id).Name);

            Assert.Equal<string>(
                expectedResults.OrderBy(n => n),
                actualResults.OrderBy(n => n));
        }

        #endregion

        #region Helpers

        private Solution CreateSolutionFromReferenceMap(string projectReferences)
        {
            Solution solution = CreateSolution();

            var references = new Dictionary<string, IEnumerable<string>>();

            var projectDefinitions = projectReferences.Split(' ');
            foreach (var projectDefinition in projectDefinitions)
            {
                var projectDefinitionParts = projectDefinition.Split(':');
                string[] referencedProjectNames = null;

                if (projectDefinitionParts.Length == 2)
                {
                    referencedProjectNames = projectDefinitionParts[1].Split(',');
                }
                else if (projectDefinitionParts.Length != 1)
                {
                    throw new ArgumentException("Invalid project definition: " + projectDefinition);
                }

                string projectName = projectDefinitionParts[0];
                if (referencedProjectNames != null)
                {
                    references.Add(projectName, referencedProjectNames);
                }

                solution = AddProject(solution, projectName);
            }

            foreach (var kvp in references)
            {
                solution = AddProjectReferences(solution, kvp.Key, kvp.Value);
            }

            return solution;
        }

        private static Solution AddProject(Solution solution, string projectName)
        {
            ProjectId projectId = ProjectId.CreateNewId(debugName: projectName);
            return solution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp, projectName));
        }

        private static Solution AddProjectReferences(Solution solution, string projectName, IEnumerable<string> projectReferences)
        {
            return solution.AddProjectReferences(
                solution.GetProjectsByName(projectName).Single().Id,
                projectReferences.Select(name => new ProjectReference(solution.GetProjectsByName(name).Single().Id)));
        }

        private Solution CreateSolution()
        {
            return new AdhocWorkspace().CurrentSolution;
        }

        #endregion
    }
}

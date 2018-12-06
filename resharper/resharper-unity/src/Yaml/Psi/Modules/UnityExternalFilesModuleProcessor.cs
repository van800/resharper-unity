using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Application.changes;
using JetBrains.Application.FileSystemTracker;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.DataFlow;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Properties;
using JetBrains.ProjectModel.Properties.Common;
using JetBrains.ProjectModel.Tasks;
using JetBrains.ProjectModel.Transaction;
using JetBrains.ReSharper.Plugins.Unity.ProjectModel;
using JetBrains.ReSharper.Plugins.Yaml.Settings;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Modules.ExternalFileModules;
using JetBrains.Util;
using JetBrains.Util.dataStructures;

namespace JetBrains.ReSharper.Plugins.Unity.Yaml.Psi.Modules
{
    [SolutionComponent]
    public class UnityExternalFilesModuleProcessor : IChangeProvider, IUnityReferenceChangeHandler
    {
        private readonly Lifetime myLifetime;
        private readonly ISolution mySolution;
        private readonly ChangeManager myChangeManager;
        private readonly IShellLocks myLocks;
        private readonly IFileSystemTracker myFileSystemTracker;
        private readonly ProjectFilePropertiesFactory myProjectFilePropertiesFactory;
        private readonly UnityYamlPsiSourceFileFactory myPsiSourceFileFactory;
        private readonly UnityExternalFilesModuleFactory myModuleFactory;
        private readonly AssetSerializationMode myAssetSerializationMode;
        private readonly YamlSupport myYamlSupport;
        private readonly JetHashSet<FileSystemPath> myRootPaths;
        private readonly FileSystemPath mySolutionDirectory;

        public UnityExternalFilesModuleProcessor(Lifetime lifetime, ISolution solution, ChangeManager changeManager,
                                                 IShellLocks locks,
                                                 ISolutionLoadTasksScheduler scheduler,
                                                 IFileSystemTracker fileSystemTracker,
                                                 ProjectFilePropertiesFactory projectFilePropertiesFactory,
                                                 UnityYamlPsiSourceFileFactory psiSourceFileFactory,
                                                 UnityExternalFilesModuleFactory moduleFactory,
                                                 AssetSerializationMode assetSerializationMode,
                                                 YamlSupport yamlSupport)
        {
            myLifetime = lifetime;
            mySolution = solution;
            myChangeManager = changeManager;
            myLocks = locks;
            myFileSystemTracker = fileSystemTracker;
            myProjectFilePropertiesFactory = projectFilePropertiesFactory;
            myPsiSourceFileFactory = psiSourceFileFactory;
            myModuleFactory = moduleFactory;
            myAssetSerializationMode = assetSerializationMode;
            myYamlSupport = yamlSupport;

            changeManager.RegisterChangeProvider(lifetime, this);

            myRootPaths = new JetHashSet<FileSystemPath>();

            // SolutionDirectory isn't absolute in tests, and will throw an exception if we use it when we call Exists
            mySolutionDirectory = solution.SolutionDirectory;
            if (!mySolutionDirectory.IsAbsolute)
                mySolutionDirectory = solution.SolutionDirectory.ToAbsolutePath(FileSystemUtil.GetCurrentDirectory());

            scheduler.EnqueueTask(new SolutionLoadTask(GetType().Name + ".Activate",
                SolutionLoadTaskKinds.PreparePsiModules,
                () => myChangeManager.AddDependency(myLifetime, mySolution.PsiModules(), this)));
        }

        public void OnUnityProjectAdded(Lifetime projectLifetime, IProject project)
        {
            // Do nothing if we don't have text based projects, and if we don't have a project with assets.
            // We could process .meta files here, as they are always written as text, but there's no point - the meta
            // file guid cache is only used in conjunction with features that require YAML files
            if (!myAssetSerializationMode.IsForceText || !myYamlSupport.IsParsingEnabled.Value ||
                !project.IsUnityGeneratedProject())
            {
                return;
            }

            var builder = new PsiModuleChangeBuilder();

            // These are idempotent and can be called multiple times
            ProcessSolutionDirectory(builder, "Assets");
            ProcessSolutionDirectory(builder, "Packages");
            ProcessSolutionDirectory(builder, "ProjectSettings");

            if (project.IsProjectFromUserView())
                ProcessDirectory(builder, project.Location);

            // Add a module reference to the project, so our reference can "see" the target (more accurately, I think
            // this is used to figure out the search domain for Find Usages)
            AddModuleReference(builder, project);

            FlushChanges(builder);
        }

        private void ProcessSolutionDirectory(PsiModuleChangeBuilder builder, string relativePath)
        {
            var path = mySolutionDirectory.Combine(relativePath);
            if (path.ExistsDirectory)
                ProcessDirectory(builder, path);
        }

        private void ProcessDirectory(PsiModuleChangeBuilder builder, FileSystemPath directory)
        {
            if (myRootPaths.Contains(directory))
                return;

            // Make sure the directory hasn't already been processed. This can happen if the project is a .asmdef based
            // project living under Assets or Packages, or inside a file:// based package
            foreach (var rootPath in myRootPaths)
            {
                if (rootPath.IsPrefixOf(directory))
                    return;
            }

            var projectFilesToAdd = new FrugalLocalList<FileSystemPath>();

            // Don't use up valuable interning spaces for files that aren't part of a project
            var files = directory.GetChildFiles("*", PathSearchFlags.RecurseIntoSubdirectories,
                FileSystemPathInternStrategy.TRY_GET_INTERNED_BUT_DO_NOT_INTERN);
            foreach (var file in files)
            {
                var extension = file.ExtensionWithDot;
                if (file.IsMeta())
                {
                    // Full path doesn't allocate
                    var fullPath = file.FullPath;
                    if (fullPath.EndsWith(".cs.meta", StringComparison.CurrentCultureIgnoreCase)
                        || fullPath.EndsWith(".prefab.meta", StringComparison.InvariantCultureIgnoreCase)
                        || fullPath.EndsWith(".unity.meta", StringComparison.InvariantCultureIgnoreCase))
                    {
                        AddMetaPsiSourceFile(builder, file);
                    }
                }
                else if (UnityYamlFileExtensions.Contains(extension))
                    projectFilesToAdd.Add(file);
            }

            AddAssetProjectFiles(projectFilesToAdd);

            myFileSystemTracker.AdviseDirectoryChanges(myLifetime, directory, true, OnProjectDirectoryChange);

            myRootPaths.Add(directory);
        }

        private void AddMetaPsiSourceFile(PsiModuleChangeBuilder builder, FileSystemPath path)
        {
            Assertion.AssertNotNull(myModuleFactory.PsiModule, "myModuleFactory.PsiModule != null");
            if (myModuleFactory.PsiModule.ContainsPath(path))
                return;

            var sourceFile = myPsiSourceFileFactory.CreateExternalPsiSourceFile(myModuleFactory.PsiModule, path);
            builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Added);
        }

        private void AddAssetProjectFiles(FrugalLocalList<FileSystemPath> paths)
        {
            if (paths.IsEmpty)
                return;

            // Add the asset file as a project file, as various features require IProjectFile. Once created, it will
            // automatically get an IPsiSourceFile created for it, and attached to our module via
            // UnityMiscFilesProjectPsiModuleProvider
            using (new ProjectModelBatchChangeCookie(mySolution, SimpleTaskExecutor.Instance))
            {
                using (mySolution.Locks.UsingWriteLock())
                {
                    foreach (var path in paths)
                    {
                        if (mySolution.FindProjectItemsByLocation(path).Count > 0)
                            continue;
                        var projectImpl = mySolution.MiscFilesProject as ProjectImpl;
                        Assertion.AssertNotNull(projectImpl, "mySolution.MiscFilesProject as ProjectImpl");
                        var properties = myProjectFilePropertiesFactory.CreateProjectFileProperties(
                            new MiscFilesProjectProperties());
                        projectImpl.DoCreateProjectFile(path, properties);
                    }
                }
            }
        }

        private void OnProjectDirectoryChange(FileSystemChangeDelta delta)
        {
            var builder = new PsiModuleChangeBuilder();
            var projectFilesToAdd = new FrugalLocalList<FileSystemPath>();
            ProcessFileSystemChangeDelta(delta, builder, projectFilesToAdd);
            AddAssetProjectFiles(projectFilesToAdd);
            FlushChanges(builder);
        }

        private void ProcessFileSystemChangeDelta(FileSystemChangeDelta delta, PsiModuleChangeBuilder builder,
                                                  FrugalLocalList<FileSystemPath> projectFilesToAdd)
        {
            var module = myModuleFactory.PsiModule;
            if (module == null)
                return;

            IPsiSourceFile sourceFile;
            switch (delta.ChangeType)
            {
                case FileSystemChangeType.ADDED:
                    if (delta.NewPath.IsAsset())
                        projectFilesToAdd.Add(delta.NewPath);
                    else if (delta.NewPath.IsMeta())
                        AddMetaPsiSourceFile(builder, delta.NewPath);
                    break;

                case FileSystemChangeType.DELETED:
                    sourceFile = GetYamlPsiSourceFile(module, delta.OldPath);
                    if (sourceFile != null)
                        builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Removed);
                    break;

                case FileSystemChangeType.CHANGED:
                    sourceFile = GetYamlPsiSourceFile(module, delta.NewPath);
                    if (sourceFile != null)
                        builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Modified);
                    break;

                case FileSystemChangeType.SUBTREE_CHANGED:
                case FileSystemChangeType.RENAMED:
                case FileSystemChangeType.UNKNOWN:
                    break;
            }

            foreach (var child in delta.GetChildren())
                ProcessFileSystemChangeDelta(child, builder, projectFilesToAdd);
        }

        [CanBeNull]
        private IPsiSourceFile GetYamlPsiSourceFile(IPsiModuleOnFileSystemPaths module, FileSystemPath path)
        {
            return module.TryGetFileByPath(path, out var sourceFile) ? sourceFile : null;
        }

        private void FlushChanges(PsiModuleChangeBuilder builder)
        {
            if (builder.IsEmpty)
                return;

            myLocks.ExecuteOrQueueEx(myLifetime, GetType().Name + ".FlushChanges",
                () =>
                {
                    var module = myModuleFactory.PsiModule;
                    Assertion.AssertNotNull(module, "module != null");
                    myLocks.AssertMainThread();
                    using (myLocks.UsingWriteLock())
                    {
                        foreach (var fileChange in builder.Result.FileChanges)
                        {
                            var location = fileChange.Item.GetLocation();
                            if (location.IsEmpty)
                                continue;

                            switch (fileChange.Type)
                            {
                                case PsiModuleChange.ChangeType.Added:
                                    module.Add(location, fileChange.Item, null);
                                    break;
                                case PsiModuleChange.ChangeType.Removed:
                                    module.Remove(location);
                                    break;
                            }
                        }

                        myChangeManager.OnProviderChanged(this, builder.Result, SimpleTaskExecutor.Instance);
                    }
                });
        }

        private void AddModuleReference(PsiModuleChangeBuilder builder, IProject project)
        {
            var thisModule = myModuleFactory.PsiModule;
            if (thisModule == null)
                return;

            foreach (var projectModule in project.GetPsiModules())
                thisModule.AddModuleReference(projectModule);

            builder.AddModuleChange(thisModule, PsiModuleChange.ChangeType.Modified);
        }

        public object Execute(IChangeMap changeMap) => null;
    }
}
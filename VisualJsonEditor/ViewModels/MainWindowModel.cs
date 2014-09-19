﻿//-----------------------------------------------------------------------
// <copyright file="MainWindowModel.cs" company="Visual JSON Editor">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>http://visualjsoneditor.codeplex.com/license</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using MyToolkit.Command;
using MyToolkit.Composition;
using MyToolkit.Data;
using MyToolkit.Messaging;
using MyToolkit.Model;
using MyToolkit.Mvvm;
using VisualJsonEditor.Localization;
using VisualJsonEditor.Messages;
using VisualJsonEditor.Model;

namespace VisualJsonEditor.ViewModels
{
    public class MainWindowModel : ViewModelBase
    {
        private JsonDocument _selectedDocument;
        private ApplicationConfiguration _configuration;

        public MainWindowModel()
        {
            Documents = new ObservableCollection<JsonDocument>();

            CreateDocumentCommand = new AsyncRelayCommand(CreateDocumentAsync);
            OpenDocumentCommand = new AsyncRelayCommand(OpenDocumentAsync);
            OpenDocumentFromPathCommand = new AsyncRelayCommand<string>(OpenDocumentAsync);
            SaveDocumentCommand = new AsyncRelayCommand<JsonDocument>(SaveDocumentAsync, d => d != null && d.UndoRedoManager.CanUndo);
            SaveDocumentAsCommand = new AsyncRelayCommand<JsonDocument>(SaveDocumentAsAsync, d => d != null);
            SaveDocumentSchemaAsCommand = new AsyncRelayCommand<JsonDocument>(SaveDocumentSchemaAsAsync, d => d != null);
            CloseDocumentCommand = new AsyncRelayCommand<JsonDocument>(CloseDocumentAsync, d => d != null);
            ValidateDocumentCommand = new AsyncRelayCommand<JsonDocument>(ValidateDocumentAsync, d => d != null);

            UndoCommand = new RelayCommand<JsonDocument>(d => d.UndoRedoManager.Undo(), d => d != null && d.UndoRedoManager.CanUndo);
            RedoCommand = new RelayCommand<JsonDocument>(d => d.UndoRedoManager.Redo(), d => d != null && d.UndoRedoManager.CanRedo);

#if DEBUG
            if (Debugger.IsAttached)
                OpenDocumentAsync(@"Samples/Sample.json");
#endif
        }

        public Strings Strings
        {
            get { return new Strings(); }
        }

        /// <summary>Gets or sets the application configuration. </summary>
        public ApplicationConfiguration Configuration
        {
            get { return _configuration; }
            set { Set(ref _configuration, value); }
        }

        /// <summary>Gets the command to open a document from a file path. </summary>
        public AsyncRelayCommand<string> OpenDocumentFromPathCommand { get; set; }

        /// <summary>Gets the command to undo the last action. </summary>
        public RelayCommand<JsonDocument> UndoCommand { get; private set; }

        /// <summary>Gets the command to redo the last action. </summary>
        public RelayCommand<JsonDocument> RedoCommand { get; set; }

        /// <summary>Gets the command to close a document. </summary>
        public AsyncRelayCommand<JsonDocument> CloseDocumentCommand { get; private set; }

        /// <summary>Gets the command to validate a document. </summary>
        public AsyncRelayCommand<JsonDocument> ValidateDocumentCommand { get; private set; }

        /// <summary>Gets the command to create a new document. </summary>
        public AsyncRelayCommand CreateDocumentCommand { get; private set; }

        /// <summary>Gets the command to save a document. </summary>
        public AsyncRelayCommand<JsonDocument> SaveDocumentCommand { get; private set; }

        /// <summary>Gets the command to save a copy of a document. </summary>
        public AsyncRelayCommand<JsonDocument> SaveDocumentAsCommand { get; private set; }

        /// <summary>Gets the command to save a copy of a document schema. </summary>
        public AsyncRelayCommand<JsonDocument> SaveDocumentSchemaAsCommand { get; private set; }

        /// <summary>Gets the command to open a document with the file open dialog. </summary>
        public AsyncRelayCommand OpenDocumentCommand { get; private set; }

        /// <summary>Gets the list of opened documents. </summary>
        public ObservableCollection<JsonDocument> Documents { get; private set; }

        /// <summary>Gets or sets the currently selected document. </summary>
        public JsonDocument SelectedDocument
        {
            get { return _selectedDocument; }
            set
            {
                if (Set(ref _selectedDocument, value))
                {
                    ValidateDocumentCommand.RaiseCanExecuteChanged();
                    CloseDocumentCommand.RaiseCanExecuteChanged();

                    UndoCommand.RaiseCanExecuteChanged();
                    RedoCommand.RaiseCanExecuteChanged();

                    SaveDocumentCommand.RaiseCanExecuteChanged();
                    SaveDocumentAsCommand.RaiseCanExecuteChanged();
                    SaveDocumentSchemaAsCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Closes the given document and saves it if needed. </summary>
        /// <param name="document">The document to close. </param>
        /// <returns>The task. </returns>
        public async Task<bool> CloseDocumentAsync(JsonDocument document)
        {
            if (document.UndoRedoManager.CanUndo)
            {
                var message = new TextMessage(string.Format(Strings.MessageSaveDocumentText, document.DisplayTitle),
                    Strings.MessageSaveDocumentTitle, MessageButton.YesNoCancel);

                var result = await Messenger.Default.SendAsync(message);
                if (result.Result == MessageResult.Cancel)
                    return false;

                if (result.Result == MessageResult.Yes)
                    await SaveDocumentAsync(document);
            }

            RemoveDocument(document);
            return true;
        }

        /// <summary>Creates a new document from a given JSON schema. </summary>
        /// <param name="schemaPath">The path of the JSON schema file. </param>
        /// <returns>The task. </returns>
        public async Task CreateDocumentAsync(string schemaPath)
        {
            await RunTaskAsync(async token =>
            {
                var document = await JsonDocument.CreateAsync(schemaPath, ServiceLocator.Default.Resolve<IDispatcher>());
                AddDocument(document);
            });
        }

        /// <summary>Opens a document with the file open dialog. </summary>
        /// <returns>The task. </returns>
        public async Task OpenDocumentAsync()
        {
            var result = await Messenger.Default.SendAsync(new OpenJsonDocumentMessage(Strings.OpenJsonDocumentDialog));
            if (result.Success)
                await OpenDocumentAsync(result.Result);
        }

        /// <summary>Opens a document from a given file name. </summary>
        /// <param name="fileName">The file name. </param>
        /// <returns>The task. </returns>
        public Task OpenDocumentAsync(string fileName)
        {
            var isReadOnly = File.GetAttributes(fileName).HasFlag(FileAttributes.ReadOnly);
            return OpenDocumentAsync(fileName, isReadOnly);
        }

        /// <summary>Opens a document from a given file name. </summary>
        /// <param name="fileName">The file name. </param>
        /// <param name="isReadOnly">The value indicating whether the document can be changed. </param>
        /// <returns>The task. </returns>
        public async Task OpenDocumentAsync(string fileName, bool isReadOnly)
        {
            var existingDocument = Documents.SingleOrDefault(d => d.FilePath == fileName);
            if (existingDocument != null)
                SelectedDocument = existingDocument;
            else
            {
                await RunTaskAsync(async token =>
                {
                    var document = await JsonDocument.LoadAsync(fileName, ServiceLocator.Default.Resolve<IDispatcher>());
                    document.IsReadOnly = isReadOnly;

                    AddDocument(document);
                    AddRecentFile(fileName);
                });
            }
        }

        /// <summary>Handles an exception which occured in the <see cref="M:MyToolkit.Mvvm.ViewModelBase.RunTaskAsync(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})"/> method. </summary>
        /// <param name="exception">The exception. </param>
        public override void HandleException(Exception exception)
        {
            LocalizationHelper.ShowError(exception);
        }

        private async Task CreateDocumentAsync()
        {
            var result = await Messenger.Default.SendAsync(new OpenJsonDocumentMessage(Strings.OpenJsonSchemaDocumentDialog));
            if (result.Success)
                await CreateDocumentAsync(result.Result);
        }

        private void AddDocument(JsonDocument document)
        {
            Documents.Add(document);
            SelectedDocument = document;

            document.UndoRedoManager.PropertyChanged += UndoRedoManagerOnPropertyChanged;
        }

        private void RemoveDocument(JsonDocument document)
        {
            Documents.Remove(document);

            document.UndoRedoManager.PropertyChanged -= UndoRedoManagerOnPropertyChanged;
        }

        private async Task ValidateDocumentAsync(JsonDocument document)
        {
            var result = await document.Data.IsValidAsync();
            if (result)
                await Messenger.Default.SendAsync(
                    new TextMessage(string.Format(Strings.MessageValidDocumentText, document.DisplayTitle), Strings.MessageValidDocumentTitle));
            else
                await Messenger.Default.SendAsync(
                    new TextMessage(string.Format(Strings.MessageNotValidDocumentText, document.DisplayTitle), Strings.MessageNotValidDocumentTitle));
        }

        private void UndoRedoManagerOnPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.IsProperty<UndoRedoManager>(i => i.CanUndo))
            {
                UndoCommand.RaiseCanExecuteChanged();

                SaveDocumentCommand.RaiseCanExecuteChanged();
                SaveDocumentAsCommand.RaiseCanExecuteChanged();
            }

            if (args.IsProperty<UndoRedoManager>(i => i.CanRedo))
                RedoCommand.RaiseCanExecuteChanged();
        }

        private void AddRecentFile(string fileName)
        {
            foreach (var entry in Configuration.RecentFiles.Where(f => f.FilePath == fileName).ToArray())
                Configuration.RecentFiles.Remove(entry);

            Configuration.RecentFiles.Insert(0, new RecentFile { FilePath = fileName });

            if (Configuration.RecentFiles.Count > 10)
                Configuration.RecentFiles.Remove(Configuration.RecentFiles.Last());
        }

        private Task SaveDocumentAsync(JsonDocument document)
        {
            return SaveDocumentAsync(document, false);
        }

        private Task SaveDocumentAsAsync(JsonDocument document)
        {
            return SaveDocumentAsync(document, true);
        }

        private async Task SaveDocumentAsync(JsonDocument document, bool saveAs)
        {
            if (!document.HasFileLocation || saveAs)
            {
                var fileName = document.HasFileLocation ?
                    Path.GetFileNameWithoutExtension(document.FilePath) + ".json" :
                    Strings.DefaultFileName + ".json";

                var result = await Messenger.Default.SendAsync(new SaveJsonDocumentMessage(fileName));
                if (result.Success)
                {
                    AddRecentFile(result.Result);
                    document.FilePath = result.Result;
                }
                else
                    return;
            }

            await RunTaskAsync(async token =>
            {
                await document.SaveAsync(true);
            });
        }

        private async Task SaveDocumentSchemaAsAsync(JsonDocument document)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath) + ".schema.json";
            var result = await Messenger.Default.SendAsync(new SaveJsonDocumentMessage(fileName));
            if (result.Success)
            {
                await RunTaskAsync(async token =>
                {
                    await Task.Run(() => File.WriteAllText(result.Result, document.Data.Schema.ToString()), token);
                });
            }
        }
    }
}

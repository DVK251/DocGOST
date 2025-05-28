/*
 *
 * This file is part of the DocGOST project.    
 * Copyright (C) 2018 Vitalii Nechaev.
 * 
 * This program is free software; you can redistribute it and/or modify it 
 * under the terms of the GNU Affero General Public License version 3 as 
 * published by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the 
 * GNU Affero General Public License for more details.
 * 
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>. *
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using Microsoft.Win32;
using DocGOST.Data;
using DocGOST.Utils;
using System.Diagnostics;
using static DocGOST.Global;


namespace DocGOST
{
    /// <summary>
    /// В программе производится работа с проектом, который представляет собой базу данных SQLite, сохранённую с расширением .DocGOST.
    /// Эта база данных состоит из нескольких талиц.
    /// 1) Таблица класса PerechenItem предназначена для хранения данных перечня элементов.
    /// 2) Табилца класса SpecificationItem предназначена для хранения данных спецификации.
    /// В этих двух таблицах ключевые элементы id представляют собой переменную типа int,
    /// которая комбинирует номер строки документа (первые 20 бит) и номер временного сохранения (последние 12 бит).
    /// Номер временного сохранения больше или равен 1 и нужен для функционирования кнопок "Отмена" и "Возврат".
    /// При сохранении проекта данные текущего временного сохранения записываются с номером сохранения 0.
    /// При закрытии проекта все сохранения, кроме нулевого, удаляются из базы данных прокета.
    /// 3) Таблица класса OsnNadpis предназначена для хранения граф основной надписи проекта.
    /// 
    /// Кроме того, программа использует другую базу данных, в которой есть таблица с данными класса DesignatorDescriptionItem.
    /// Эта база данных должна находиться в одной папке с exe-файлом программы и её данные не зависят от отрытого в данный момент проекта.
    /// Она предназначена для расшифровки позиционных обозначений компонентов (например, строка "C" "Конденсатор" "Конденсаторы"). При
    /// изменении этой базы данных эти изменения коснутся не только текущего проекта, но и всех проектов, которые будут открыты после её изменения.
    /// </summary>
    public partial class MainWindow : Window
    {
        TempSaves perTempSave, specTempSave, pcbSpecTempSave, vedomostTempSave; //переменные для работы с номерами временных сохранений (для работы кнопок "Отменить"/"Вернуть")
        Global id; //Переменная для работы с id данных проекта (т.е. для того, чтобы создавать и расшифровывать id,
                   //т.к. id сгруппирован из номера текущего сохранённого состояния и номера строки записи - подробнее в Data.PerechenItem.cs и Data.SpecificationItem.cs)
        bool isPcbMultilayer = false;
        bool bSilentMode = false; // режим запуска из командной строки без выдачи запроса при закрытии программы и без автоматического открытия сформированных pdf-файлов

        class HiePerechenBlock
        {
            public List<int> sameBlockNums;
            public List<PerechenItem> perechenItems;
        }

        public MainWindow()
        {
            InitializeComponent();

            var Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Title += " v" + Version.ToString(3);

            Application.Current.MainWindow.WindowState = WindowState.Maximized;

            id = new Global();

            CommandBinding undoBinding = new CommandBinding(ApplicationCommands.Undo);
            undoBinding.Executed += UndoButton_Click;
            this.CommandBindings.Add(undoBinding);

            CommandBinding redoBinding = new CommandBinding(ApplicationCommands.Redo);
            redoBinding.Executed += RedoButton_Click;
            this.CommandBindings.Add(redoBinding);

            CommandBinding saveBinding = new CommandBinding(ApplicationCommands.Save);
            saveBinding.Executed += SaveProject_Click;
            this.CommandBindings.Add(saveBinding);

            CommandBinding openBinding = new CommandBinding(ApplicationCommands.Open);
            openBinding.Executed += OpenProject_Click;
            this.CommandBindings.Add(openBinding);

            CommandBinding newBinding = new CommandBinding(ApplicationCommands.New);
            newBinding.Executed += CreateProject_Click;
            this.CommandBindings.Add(newBinding);

            CommandBinding closeBinding = new CommandBinding(ApplicationCommands.Close);
            closeBinding.Executed += ExitMenuItem_Click;
            this.CommandBindings.Add(closeBinding);

            try { 
                bool ReadParamIfName(string arg, string name, ref string value) {
                    bool rslt = arg.StartsWith("/" + name + "=", StringComparison.CurrentCultureIgnoreCase);
                    if (rslt)
                        value = arg.Substring(name.Length + 2);
                    return rslt;
                }

                string importfn = "";
                string exportmode = "";
                string signDate = "";
                string orgname = "";
                string bExit = "";
                var SA = System.Environment.GetCommandLineArgs();
                for (int i = 1; i < SA.Length; i++) { // skip 1st param
                    string arg = SA[i];
                    if (ReadParamIfName(arg, "import", ref importfn)) continue;
                    if (ReadParamIfName(arg, "export", ref exportmode)) continue;
                    if (ReadParamIfName(arg, "date", ref signDate)) continue;
                    if (ReadParamIfName(arg, "orgname", ref orgname)) continue;
                    if (ReadParamIfName(arg, "exit", ref bExit)) continue;
                    throw new Exception($"Неверный аргумент '{arg}'");
                }
                if (importfn != "") {
                    bSilentMode = true;
                    ImportPrjfromMentor(importfn, signDate, orgname); 
                    exportmode = exportmode.ToUpper();
                    if (exportmode != "") {
                        if (exportmode.Contains("P")) chkExportPerechen.IsChecked = true;
                        if (exportmode.Contains("S")) chkExportSpec.IsChecked = true;
                        if (exportmode.Contains("V")) chkExportVedomost.IsChecked = true;
                        if (exportmode.Contains("i")) addListRegistrCheckBox.IsChecked = true;
                        CreatePdf_Click(null, null);
                    }
                    if (bExit != "") Close();
                }
            } catch (Exception ex) {
                MessageBox.Show("Ошибка при анализе командной строки. " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        string projectPath;

        static ProjectDB project; //данные для всей документации

        /// <summary> Создание нового проекта </summary>
        private void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog createDlg = new SaveFileDialog();
            createDlg.Title = "Создание проекта";
            createDlg.Filter = "Файлы проекта (*.docGOST)|*.docGOST";
            createDlg.OverwritePrompt = true;
            createDlg.FileName = "Проект";

            if (createDlg.ShowDialog() == false) return;
            CreateProject(createDlg.FileName);
        }

        void CreateProject(string filename) 
        { 
            this.projectPath = filename;
            //Удаляем все элементы из дерева проектов
            for (int i = 0; i < projectTreeViewItem.Items.Count; i++)
                projectTreeViewItem.Items.RemoveAt(i);
            //Называем проект в дереве проектов так же, как файл
            projectTreeViewItem.Header = Path.GetFileNameWithoutExtension(projectPath);

            TreeViewItem apparatItem = new TreeViewItem();
            apparatItem.Header = "Исполнение: [No Variations]";
            apparatItem.Name = "apparatusTreeItem1";

            TreeViewItem docItem = new TreeViewItem();
            docItem.Header = "Перечень элементов";
            docItem.IsSelected = true;
            docItem.Selected += treeSelectionChanged;
            apparatItem.Items.Add(docItem);
            docItem = new TreeViewItem();
            docItem.Header = "Спецификация";
            docItem.Selected += treeSelectionChanged;
            apparatItem.Items.Add(docItem);
            docItem = new TreeViewItem();
            docItem.Header = "Ведомость";
            docItem.Selected += treeSelectionChanged;
            apparatItem.Items.Add(docItem);
            docItem = new TreeViewItem();
            docItem.Header = "Спецификация ПП";
            docItem.Selected += treeSelectionChanged;
            apparatItem.Items.Add(docItem);
            projectTreeViewItem.Items.Add(apparatItem);

            projectTreeViewItem.ExpandSubtree();

            project?.Dispose();

            if (File.Exists(projectPath))
            {
                File.Delete(projectPath);
            }

            project = new ProjectDB(projectPath);

            //Очищаем рабочую область от предыдущих значений
            DisplayPerValues(null);
            DisplaySpecValues(null);
            DisplayVedomostValues(null);
            DisplayPcbSpecValues(null);

            importPrjPcbfromADMenuItem.IsEnabled = true;
            importPrjfromKiCadMenuItem.IsEnabled = true;
            osnNadpisMenuItem.IsEnabled = true;
            osnNadpisButton.IsEnabled = true;

            CreateEmptyProject();
        }

        /// <summary> Переключение между документами в дереве проекта </summary>
        private void treeSelectionChanged(object sender, RoutedEventArgs e)
        {
            string header = (sender as TreeViewItem).Header.ToString();
            if (header == "Перечень элементов")
            {
                perechenListView.Visibility = Visibility.Visible;
                specificationTabControl.Visibility = Visibility.Hidden;
                pcbSpecificationTabControl.Visibility = Visibility.Hidden;
                vedomostListView.Visibility = Visibility.Hidden;
            }
            else if (header == "Спецификация")
            {
                perechenListView.Visibility = Visibility.Hidden;
                specificationTabControl.Visibility = Visibility.Visible;
                pcbSpecificationTabControl.Visibility = Visibility.Hidden;
                vedomostListView.Visibility = Visibility.Hidden;
            }
            else if (header == "Ведомость")
            {
                perechenListView.Visibility = Visibility.Hidden;
                specificationTabControl.Visibility = Visibility.Hidden;
                pcbSpecificationTabControl.Visibility = Visibility.Hidden;
                vedomostListView.Visibility = Visibility.Visible;
            }
            else if (header == "Спецификация ПП")
            {
                perechenListView.Visibility = Visibility.Hidden;
                specificationTabControl.Visibility = Visibility.Hidden;
                pcbSpecificationTabControl.Visibility = Visibility.Visible;
                vedomostListView.Visibility = Visibility.Hidden;
            }
        }

        /// <summary> Открытие существующего проекта </summary>
        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            waitMessageLabel.Content = "Подождите, проект открывается. Это может занять несколько минут...";
            waitGrid.Visibility = Visibility.Visible;
            try { 
                OpenFileDialog openDlg = new OpenFileDialog();
                openDlg.Title = "Выбор файла проекта";
                openDlg.Multiselect = false;
                openDlg.Filter = "Файлы проекта (*.docGOST)|*.docGOST";
                if (openDlg.ShowDialog() != true) return;

                MessageBoxResult closingDialogResult = MessageBoxResult.No;
                if ((perTempSave != null) && (specTempSave != null) && (pcbSpecTempSave != null) && (vedomostTempSave != null))
                {
                    if ((perTempSave.GetCurrent() != perTempSave.GetLastSavedState()) |
                    (specTempSave.GetCurrent() != specTempSave.GetLastSavedState()) |
                    (pcbSpecTempSave.GetCurrent() != pcbSpecTempSave.GetLastSavedState()) |
                    (vedomostTempSave.GetCurrent() != vedomostTempSave.GetLastSavedState()))
                    {
                        closingDialogResult = MessageBox.Show("Проект не сохранён. Сохранить проект перед закрытием?", "Сохранение проекта", MessageBoxButton.YesNoCancel);
                        if (closingDialogResult == MessageBoxResult.Yes) 
                            project.Save(perTempSave.GetCurrent(), specTempSave.GetCurrent(), vedomostTempSave.GetCurrent(), pcbSpecTempSave.GetCurrent());
                        else if (closingDialogResult == MessageBoxResult.Cancel)
                            return;
                    }
                    project.DeleteTempData();
                }

                if (closingDialogResult != MessageBoxResult.Cancel)
                {
                    projectPath = openDlg.FileName;
                    project?.Dispose();
                    project = new Data.ProjectDB(projectPath);

                    perTempSave = new TempSaves();
                    specTempSave = new TempSaves();
                    vedomostTempSave = new TempSaves();
                    pcbSpecTempSave = new TempSaves();

                    //Удаляем все элементы из дерева проектов
                    for (int i = 0; i < projectTreeViewItem.Items.Count; i++)
                        projectTreeViewItem.Items.RemoveAt(i);
                    //Называем проект в дереве проектов так же, как файл
                    int length = openDlg.SafeFileName.Length;
                    projectTreeViewItem.Header = openDlg.SafeFileName.Substring(0, length - 8);

                    //Читаем из свойств проекта название исполнения
                    string variantName = String.Empty;

                    if (project.GetParameterItem("Variant") != null)
                    {
                        variantName = project.GetParameterItem("Variant").value;
                    }
                    else variantName = "[No Variations]";

                    if (project.GetParameterItem("isPcbMultilayer") != null)
                    {
                        if (project.GetParameterItem("isPcbMultilayer").value.ToLower() == "true")
                            isPcbMultilayer = true;
                    }
                    else //если "false"
                        isPcbMultilayer = false;


                    //Заполняем дерево проекта
                    TreeViewItem apparatItem = new TreeViewItem();
                    apparatItem.Header = "Исполнение: " + variantName;
                    apparatItem.Name = "apparatusTereeItem1";

                    TreeViewItem docItem = new TreeViewItem();
                    docItem.Header = "Перечень элементов";
                    docItem.IsSelected = true;
                    docItem.Selected += treeSelectionChanged;
                    apparatItem.Items.Add(docItem);
                    docItem = new TreeViewItem();
                    docItem.Header = "Спецификация";
                    docItem.Selected += treeSelectionChanged;
                    apparatItem.Items.Add(docItem);
                    docItem = new TreeViewItem();
                    docItem.Header = "Ведомость";
                    docItem.Selected += treeSelectionChanged;
                    apparatItem.Items.Add(docItem);

                    if (isPcbMultilayer == true)
                    {
                        docItem = new TreeViewItem();
                        docItem.Header = "Спецификация ПП";
                        docItem.Selected += treeSelectionChanged;
                        apparatItem.Items.Add(docItem);
                    }

                    projectTreeViewItem.Items.Add(apparatItem);

                    projectTreeViewItem.ExpandSubtree();


                    //Устанавливаем состояние флажков параметров экспорта в pdf
                    if (project.GetParameterItem("isListRegistrChecked") == null)
                        addListRegistrCheckBox.IsChecked = true;
                    else if (project.GetParameterItem("isListRegistrChecked").value == "true")
                        addListRegistrCheckBox.IsChecked = true;
                    else //если "false"
                        addListRegistrCheckBox.IsChecked = false;


                    if (project.GetParameterItem("isStartFromSecondChecked") == null)
                        startFromSecondCheckBox.IsChecked = false;
                    else if (project.GetParameterItem("isStartFromSecondChecked").value == "false")
                        startFromSecondCheckBox.IsChecked = false;
                    else //если "true"
                        startFromSecondCheckBox.IsChecked = true;


                    //Копируем данные во временные
                    perTempSave = new TempSaves();
                    project.BeginTransaction();
                    try { 
                        var len = project.GetPerechenLength(0);
                        for (int i = 1; i <= len; i++)
                        {
                            PerechenItem perItem = new PerechenItem();
                            perItem = project.GetPerechenItem(i, 0);
                            perItem.id = id.makeID(i, perTempSave.GetCurrent());
                            project.AddPerechenItem(perItem);
                        }
                        perTempSave.ProjectSaved();

                        len = project.GetSpecLength(0);
                        for (int i = 1; i <= len; i++)
                        {
                            SpecificationItem specItem = new SpecificationItem();
                            specItem = project.GetSpecItem(i, 0);
                            specItem.id = id.makeID(i, specTempSave.GetCurrent());
                            project.AddSpecItem(specItem);
                        }
                        specTempSave.ProjectSaved();

                        len = project.GetVedomostLength(0);
                        for (int i = 1; i <= len; i++)
                        {
                            VedomostItem vedomostItem = new VedomostItem();
                            vedomostItem = project.GetVedomostItem(i, 0);
                            vedomostItem.id = id.makeID(i, specTempSave.GetCurrent());
                            project.AddVedomostItem(vedomostItem);
                        }
                        vedomostTempSave.ProjectSaved();

                        if (isPcbMultilayer)
                        {
                            len = project.GetPcbSpecLength(0);
                            for (int i = 1; i <= len; i++)
                            {
                                PcbSpecificationItem specItem = new PcbSpecificationItem();
                                specItem = project.GetPcbSpecItem(i, 0);
                                specItem.id = id.makeID(i, pcbSpecTempSave.GetCurrent());
                                project.AddPcbSpecItem(specItem);
                            }
                            pcbSpecTempSave.ProjectSaved();
                        }
                        else
                        {
                            PcbSpecificationItem specItem = new PcbSpecificationItem();
                            specItem.id = id.makeID(0, pcbSpecTempSave.GetCurrent());
                            project.AddPcbSpecItem(specItem);

                            //((TreeViewItem)(projectTreeViewItem.Items[0])).Items.RemoveAt(((TreeViewItem)(projectTreeViewItem.Items[0])).Items.Count - 1);
                        }
                    } finally {
                        project.Commit();
                    }

                    DisplayAllValues();

                    importPrjPcbfromADMenuItem.IsEnabled = true;
                    importPrjfromKiCadMenuItem.IsEnabled = true;
                    saveProjectMenuItem.IsEnabled = true;
                    undoMenuItem.IsEnabled = true;
                    redoMenuItem.IsEnabled = true;
                    createPdfMenuItem.IsEnabled = true;
                    btnCreatePdf.IsEnabled = true;
                    osnNadpisMenuItem.IsEnabled = true;
                    osnNadpisButton.IsEnabled = true;

                }
            }
            finally { 
                waitGrid.Visibility = Visibility.Hidden;
            }
        }

        private void CreateEmptyProject()
        {
            perTempSave = new TempSaves();
            specTempSave = new TempSaves();
            pcbSpecTempSave = new TempSaves();
            vedomostTempSave = new TempSaves();


            List<List<ComponentProperties>> componentsList = new List<List<ComponentProperties>>();
            List<ComponentProperties> componentPropList = new List<ComponentProperties>();

            List<ComponentProperties> otherPropList = new List<ComponentProperties>();

            createPdfMenuItem.IsEnabled = true;
            btnCreatePdf.IsEnabled = true;

            List<PerechenItem> perechenList = new List<PerechenItem>();
            List<SpecificationItem> specList = new List<SpecificationItem>();
            List<VedomostItem> vedomostList = new List<VedomostItem>();
            List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

            int numberOfValidStrings = 0;

            SpecificationItem tempSpecItem = new SpecificationItem();
            numberOfValidStrings++;
            tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
            tempSpecItem.name = String.Empty;
            tempSpecItem.quantity = String.Empty;
            tempSpecItem.spSection = (int)Global.SpSections.Documentation;
            specList.Add(tempSpecItem);

            PcbSpecificationItem tempPcbSpecItem = new PcbSpecificationItem();
            numberOfValidStrings++;
            tempPcbSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
            tempPcbSpecItem.name = String.Empty;
            tempPcbSpecItem.quantity = String.Empty;
            tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
            pcbSpecList.Add(tempPcbSpecItem);

            PerechenItem tempPerechen = new PerechenItem();
            SpecificationItem tempSpecification = new SpecificationItem();
            VedomostItem tempVedomost = new VedomostItem();
            PcbSpecificationItem tempPcbSpecification = new PcbSpecificationItem();

            numberOfValidStrings++;
            tempPerechen.id = id.makeID(numberOfValidStrings, perTempSave.GetCurrent());
            tempPerechen.designator = string.Empty;
            tempPerechen.name = string.Empty;
            tempPerechen.quantity = String.Empty;
            tempPerechen.note = String.Empty;
            tempPerechen.docum = String.Empty;
            tempPerechen.type = String.Empty;
            tempPerechen.group = String.Empty;
            tempPerechen.groupPlural = String.Empty;
            tempPerechen.isNameUnderlined = false;

            tempSpecification.id = tempPerechen.id;
            tempSpecification.spSection = (int)Global.SpSections.Other;
            tempSpecification.format = String.Empty;
            tempSpecification.zona = String.Empty;
            tempSpecification.position = String.Empty;
            tempSpecification.oboznachenie = String.Empty;
            tempSpecification.name = tempPerechen.name;
            tempSpecification.quantity = String.Empty;
            tempSpecification.note = tempPerechen.designator;
            tempSpecification.group = String.Empty;
            tempSpecification.docum = tempPerechen.docum;
            tempSpecification.designator = tempPerechen.designator;
            tempSpecification.isNameUnderlined = false;

            tempPcbSpecification.id = tempPerechen.id;
            tempPcbSpecification.spSection = (int)Global.SpSections.Other;
            tempPcbSpecification.format = String.Empty;
            tempPcbSpecification.zona = String.Empty;
            tempPcbSpecification.position = String.Empty;
            tempPcbSpecification.oboznachenie = String.Empty;
            tempPcbSpecification.name = tempPerechen.name;
            tempPcbSpecification.quantity = String.Empty;
            tempPcbSpecification.note = tempPerechen.designator;
            tempPcbSpecification.group = String.Empty;
            tempPcbSpecification.docum = tempPerechen.docum;
            tempPcbSpecification.designator = tempPerechen.designator;
            tempPcbSpecification.isNameUnderlined = false;

            tempVedomost.id = tempPerechen.id;
            tempVedomost.designator = tempPerechen.designator;
            tempVedomost.name = tempPerechen.name;
            tempVedomost.kod = String.Empty;
            tempVedomost.docum = tempPerechen.docum;
            tempVedomost.supplier = String.Empty;
            tempVedomost.belongs = String.Empty;
            tempVedomost.quantityIzdelie = String.Empty;
            tempVedomost.quantityComplects = String.Empty;
            tempVedomost.quantityTotal = String.Empty;
            tempVedomost.note = String.Empty;
            tempVedomost.isNameUnderlined = false;

            string group = string.Empty;

            tempSpecification.name = group + " " + tempSpecification.name + " " + tempSpecification.docum;

            perechenList.Add(tempPerechen);
            specList.Add(tempSpecification);
            vedomostList.Add(tempVedomost);
            pcbSpecList.Add(tempPcbSpecification);



            //Сортировка по поз. обозначению
            List<PerechenItem> perechenListSorted = new List<PerechenItem>();
            List<SpecificationItem> specOtherListSorted = new List<SpecificationItem>();
            List<VedomostItem> vedomostListSorted = new List<VedomostItem>();
            List<PcbSpecificationItem> pcbSpecOtherListSorted = new List<PcbSpecificationItem>();


            specOtherListSorted = specList.Where(x => x.spSection == ((int)Global.SpSections.Other)).OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
            vedomostListSorted = vedomostList.OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
            pcbSpecOtherListSorted = pcbSpecList.Where(x => x.spSection == ((int)Global.SpSections.Other)).OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();

            perechenList[0].id = id.makeID(1, perTempSave.GetCurrent());
            specOtherListSorted[0].id = id.makeID(1 + (numberOfValidStrings - specOtherListSorted.Count), specTempSave.GetCurrent());
            vedomostListSorted[0].id = id.makeID(1, vedomostTempSave.GetCurrent());
            pcbSpecOtherListSorted[0].id = id.makeID(1 + (numberOfValidStrings - pcbSpecOtherListSorted.Count), pcbSpecTempSave.GetCurrent());

            saveProjectMenuItem.IsEnabled = true;
            undoMenuItem.IsEnabled = true;
            redoMenuItem.IsEnabled = true;

            groupByNameSaveAndPrint(perechenList, specList.Where(x => x.spSection != ((int)Global.SpSections.Other)).ToList(), specOtherListSorted, vedomostListSorted);

            //Сохраняем спецификацию для ПП в БД, потому что она не требует сортировки
            project.BeginTransaction();
            try { 
                for (int i = 0; i < pcbSpecList.Count; i++)
                {
                    PcbSpecificationItem sd = pcbSpecList[i];
                    sd.id = id.makeID(i + 1, pcbSpecTempSave.GetCurrent());
                    project.AddPcbSpecItem(sd);
                }
            } finally { project.Commit(); }
        }

        private void ImportPrjPcbfromAD_Click(object sender, RoutedEventArgs e)
        {
            waitMessageLabel.Content = "Подождите, импорт данных...";
            waitGrid.Visibility = Visibility.Visible;

            OpenFileDialog openDlg = new OpenFileDialog();
            openDlg.Title = "Выбор файла проекта AltiumDesigner";
            openDlg.Multiselect = false;
            openDlg.Filter = "Файлы проекта AD (*.PrjPcb)|*.PrjPcb";
            if (openDlg.ShowDialog() == true)
            {
                string pcbPrjFilePath = openDlg.FileName;

                ImportPrjPcbfromAD(pcbPrjFilePath);
                waitGrid.Visibility = Visibility.Hidden;
            }
            else
            {
                waitGrid.Visibility = Visibility.Hidden;
            }


        }


        /// <summary> Импорт данных из проекта Altium Designer (.PrjPcb) </summary>
        private void ImportPrjPcbfromAD(string pcbPrjFilePath)
        {


            perTempSave = new TempSaves();
            specTempSave = new TempSaves();
            vedomostTempSave = new TempSaves();

            List<DielProperties> pcbMaterialsList = new List<DielProperties>();

            #region Открытие и парсинг файла проекта Altium

            string pcbPrjFolderPath = System.IO.Path.GetDirectoryName(pcbPrjFilePath);

            //Открываем файл проекта AD для поиска имён файлов схемы:
            FileStream pcbPrjFile = new FileStream(pcbPrjFilePath, FileMode.Open, FileAccess.Read);
            StreamReader pcbPrjReader = new StreamReader(pcbPrjFile, System.Text.Encoding.Default);

            String prjStr = String.Empty;
            String prevPrjStr = String.Empty;
            String prevPrevPrjStr = String.Empty;

            int numberOfStrings = 0;

            List<List<ComponentProperties>> componentsList = new List<List<ComponentProperties>>();
            List<ComponentProperties> componentPropList = new List<ComponentProperties>();

            List<List<List<ComponentProperties>>> componentsVariantList = new List<List<List<ComponentProperties>>>();
            List<List<ComponentProperties>> componentsPropVariantList = new List<List<ComponentProperties>>();
            List<ComponentProperties> componentVariantPropList = new List<ComponentProperties>();
            List<String> variantNameList = new List<String>();
            List<String> dnfDesignatorsList = new List<String>();
            List<List<String>> dnfVariantDesignatorsList = new List<List<String>>();
            List<String> fittedDesignatorsList = new List<String>();
            List<List<String>> fittedVariantDesignatorsList = new List<List<String>>();

            List<List<string[]>> prjParamsVariantList = new List<List<string[]>>();


            variantNameList.Add("No Variations");
            bool isWaitingVariantDescription = false;

            List<ComponentProperties> otherPropList = new List<ComponentProperties>();

            SpecificationItem plataSpecItem = new SpecificationItem(); //для хранения записи печатной платы для добавления в раздел "Детали" или "Сборочные единицы" спецификации

            plataSpecItem.position = SpecificationItem.POS_AUTO;
            plataSpecItem.name = "Плата печатная";
            plataSpecItem.quantity = "1";
            int pcbLayersCount = 0;

            int currentVariant = 0;

            while ((prjStr = pcbPrjReader.ReadLine()) != null)
            {
                #region Запись данных об исполнениях в список
                string schPath = String.Empty;


                if (prjStr.Length > 15)
                {
                    if (prjStr.Substring(0, 15).ToUpper() == "[PROJECTVARIANT")
                    {
                        componentVariantPropList = new List<ComponentProperties>();
                        isWaitingVariantDescription = true;
                        prjStr = prjStr.Replace(']', new char());
                        currentVariant = int.Parse(prjStr.Substring(15));
                    }
                }
                if (prjStr.Length > 18)
                {
                    if (prjStr.Substring(0, 19).ToUpper() == "PARAMVARIATIONCOUNT")
                    {
                        if (componentVariantPropList.Count > 0)
                        {
                            componentsPropVariantList.Add(componentVariantPropList);
                            componentVariantPropList = new List<ComponentProperties>();
                        }
                        if (componentsPropVariantList.Count > 0)
                        {
                            componentsVariantList.Add(componentsPropVariantList);
                            componentsPropVariantList = new List<List<ComponentProperties>>();
                        }
                        dnfVariantDesignatorsList.Add(dnfDesignatorsList);
                        //MessageBox.Show(String.Join(", ", dnfDesignatorsList));
                        fittedVariantDesignatorsList.Add(fittedDesignatorsList);
                        dnfDesignatorsList = new List<String>();
                        fittedDesignatorsList = new List<String>();
                    }
                }
                if (prjStr.Length > 12)
                {
                    if ((isWaitingVariantDescription == true) && (prjStr.Substring(0, 12).ToUpper() == "DESCRIPTION="))
                    {
                        isWaitingVariantDescription = false;
                        variantNameList.Add(prjStr.Substring(12, prjStr.Length - 12));
                    }
                }

                if (prjStr.Length > 9)
                {
                    string[] prjStrArray = prjStr.Split(new Char[] { '|' });
                    string[] valuesArray = prjStrArray[0].Split(new Char[] { '=' });
                    if (valuesArray.Length > 2)
                    {

                        if (valuesArray[0].Length >= 9)
                            if (valuesArray[0].Substring(0, 9).ToUpper() == "VARIATION")
                                if (valuesArray[1].Substring(0, 10).ToUpper() == "DESIGNATOR")
                                {
                                    if (componentVariantPropList.Count > 0)
                                    {
                                        componentsPropVariantList.Add(componentVariantPropList);
                                        componentVariantPropList = new List<ComponentProperties>();
                                    }


                                    ComponentProperties prop = new ComponentProperties();
                                    prop.Name = "Designator";
                                    prop.Text = valuesArray[2];

                                    string designator = valuesArray[2];

                                    componentVariantPropList.Add(prop);

                                    if (prjStrArray.Length > 1)
                                    {
                                        valuesArray = prjStrArray[2].Split(new Char[] { '=' });
                                        if (valuesArray[0].ToUpper() == "KIND")
                                        {
                                            prop = new ComponentProperties();
                                            prop.Name = "Kind";
                                            prop.Text = valuesArray[1];

                                            componentVariantPropList.Add(prop);
                                            if (valuesArray[1] == "1") dnfDesignatorsList.Add(designator);
                                            else fittedDesignatorsList.Add(designator);
                                        }
                                    }



                                }
                        if (valuesArray[0].Length >= 14)
                            if (valuesArray[0].Substring(0, 14).ToUpper() == "PARAMVARIATION")
                                if (valuesArray[1].ToUpper() == "PARAMETERNAME")
                                {
                                    ComponentProperties prop = new ComponentProperties();
                                    prop.Name = valuesArray[2];

                                    //Считываем VariantValue
                                    valuesArray = prjStrArray[1].Split(new Char[] { '=' });
                                    prop.Text = valuesArray[1];

                                    componentVariantPropList.Add(prop);
                                }
                    }

                }
                #endregion

                if ((prevPrevPrjStr.Length > 10) && (prevPrjStr.Length > 4) && (prjStr.Length > 5))
                {
                    if (prevPrevPrjStr.Substring(0, 10).ToUpper() == "[PARAMETER")
                        if (prevPrjStr.Substring(0, 4).ToUpper() == "NAME")
                            if (prjStr.Substring(0, 5).ToUpper() == "VALUE")
                            {
                                prevPrevPrjStr = prevPrevPrjStr.Replace(']', new char());
                                string[] paramPrjStrArray = prevPrevPrjStr.Split(new Char[] { '_' });
                                int variantNumber = 0;
                                if (paramPrjStrArray.Length > 1) variantNumber = currentVariant;


                                string[] nameValue = new string[2];

                                nameValue[0] = prevPrjStr.Split('=')[1];
                                nameValue[1] = prjStr.Split('=')[1];

                                while (prjParamsVariantList.Count <= variantNumber) prjParamsVariantList.Add(new List<string[]>());


                                prjParamsVariantList[variantNumber].Add(nameValue);
                            }
                }

                if (prjStr.Length > 13)
                    if ((prjStr.Substring(0, 13).ToUpper() == "DOCUMENTPATH=") && (prjStr.Substring(prjStr.Length - 6, 6).ToUpper() == "SCHDOC"))
                    {
                        schPath = System.IO.Path.Combine(pcbPrjFolderPath, prjStr.Substring(13));

                        //Открываем файл схемы AD для получения списка параметров электронных компонентов:
                        FileStream schFile = new FileStream(schPath, FileMode.Open, FileAccess.Read);
                        StreamReader schReader = new StreamReader(schFile, System.Text.Encoding.Default);

                        string schStr = String.Empty;

                        bool isNoBom = false;
                        bool isFirstPartOfComponent = true;
                        bool isComponent = false;

                        while ((schStr = schReader.ReadLine()) != null)
                        {
                            string[] schStrArray = schStr.Split(new Char[] { '|' });
                            for (int i = 0; i < schStrArray.Length - 1; i++)
                            {
                                if (isComponent == true)
                                {
                                    if (schStrArray[i].Length >= 23)
                                        if (schStrArray[i].Substring(0, 23).ToUpper() == "COMPONENTKINDVERSION2=5") isNoBom = true;

                                    if (schStrArray[i].Length >= 15)
                                        if (schStrArray[i].Substring(0, 14).ToUpper() == "CURRENTPARTID=")
                                            if ((schStrArray[i].Substring(0, 15).ToUpper() != "CURRENTPARTID=1") | (schStrArray[i].Length > 15)) isFirstPartOfComponent = false;

                                    if ((schStrArray[i].Length > 5) && (schStrArray[i + 1].Length > 5))
                                        if ((schStrArray[i].Substring(0, 5).ToUpper() == "TEXT=") && (schStrArray[i + 1].Substring(0, 5).ToUpper() == "NAME="))
                                        {
                                            ComponentProperties prop = new ComponentProperties();
                                            prop.Name = schStrArray[i + 1].Substring(5);
                                            prop.Text = schStrArray[i].Substring(5);
                                            //prop.Text = prop.Text.Replace(char.ConvertFromUtf32(0xD192), "-"); //В проекте встретился символ "-" Юникод 0х2010, который отображался как "?" и в текстовом файле он выглядит как "?". Поэтому его заменяем.
                                            if (isNoBom == false)
                                            {
                                                //if ((prop.Name == "Designator") | (prop.Name == "SType") | (prop.Name == "Docum") | (prop.Name == "Note"))
                                                componentPropList.Add(prop);
                                            }

                                        }

                                    if (schStrArray[i].Length >= 8)
                                        if ((schStrArray[i].Substring(0, 7).ToUpper() == "HEADER=") || ((schStrArray[i].ToUpper() == "RECORD=1") && (schStrArray[i].Length == 8))) //Считаем, что описание каждого компонента заканчивается этой фразой
                                        {
                                            if ((isNoBom == false) && (componentPropList.Count > 0) && (isFirstPartOfComponent == true))
                                            {
                                                componentsList.Add(componentPropList);

                                                numberOfStrings++;
                                            }

                                            isNoBom = false;
                                            isComponent = false;
                                            isFirstPartOfComponent = true;

                                            componentPropList = new List<ComponentProperties>();
                                        }
                                }
                                else if (isComponent == false)
                                {
                                    //Запись полей основной надписи в базу данных проекта, если они встречаются в файле Sch:
                                    if ((schStrArray[i].Length > 5) && (schStrArray[i + 1].Length > 5))
                                        if ((schStrArray[i].Substring(0, 5).ToUpper() == "TEXT=") && (schStrArray[i + 1].Substring(0, 5).ToUpper() == "NAME="))
                                        {
                                            ComponentProperties prop = new ComponentProperties();
                                            prop.Name = schStrArray[i + 1].Substring(5);
                                            prop.Text = schStrArray[i].Substring(5);

                                            otherPropList.Add(prop);
                                        }
                                }
                                //Теперь ищем все записи компонента и сохраняем их
                                if (schStrArray[i].Length == 8)
                                    if (schStrArray[i].Substring(0, 8).ToUpper() == "RECORD=1") //Считаем, что описание каждого компонента начинается этой фразой
                                        isComponent = true;
                            }
                        }

                        //Работа с составными именами типа ='prop1'+'prop2'
                        for (int i = 0; i < otherPropList.Count; i++)
                        {
                            otherPropList[i].Text = MakeComplexStringIfItIs(otherPropList[i].Text, otherPropList);
                        }

                        schFile.Close();
                    }
                    else if ((prjStr.Substring(0, 13) == "DocumentPath=") && (prjStr.Substring(prjStr.Length - 6, 6) == "PcbDoc") && (pcbLayersCount == 0)) //pcbLayersCount == 0 - если в проекте 2 и более плат, считываем только данные первой
                    {
                        //Добавляем плату в спецификацию                             
                        //plataSpecItem.oboznachenie = prjStr.Substring(13, prjStr.Length - 20);
                        //plataSpecItem.oboznachenie = plataSpecItem.oboznachenie.Split(new char[] { '\\' }).Last();


                        string pcbPath = System.IO.Path.Combine(pcbPrjFolderPath, prjStr.Substring(13));

                        //Открываем файл платы AD для получения данных:
                        FileStream pcbFile = new FileStream(pcbPath, FileMode.Open, FileAccess.Read);
                        StreamReader pcbReader = new StreamReader(pcbFile, System.Text.Encoding.Default);

                        string pcbStr = String.Empty;

                        //Считываем свойства платы
                        bool isLayersCounting = false;
                        string dielLayerNumber = String.Empty;
                        List<DielProperties> pcbMaterialsTempList = new List<DielProperties>();
                        DielProperties pcbMaterialsItem = new DielProperties();

                        while ((pcbStr = pcbReader.ReadLine()) != null)
                        {
                            string[] pcbStrArray = pcbStr.Split(new Char[] { '|' });
                            for (int i = 0; i < pcbStrArray.Length - 1; i++)
                            {
                                int curStrLength = pcbStrArray[i].Length;
                                //Определяем количество слоёв платы, и определяем, к какому разделу спецификации будет отноститься плата
                                if (curStrLength == 28)
                                    if ((pcbStrArray[i].Substring(0, 8).ToUpper() == "LAYERSET") && (pcbStrArray[i].Substring(9, 19).ToUpper() == "NAME=&SIGNAL LAYERS"))
                                    {
                                        isLayersCounting = true;
                                    }
                                if (curStrLength == 27)
                                    if ((pcbStrArray[i].Substring(0, 8).ToUpper() == "LAYERSET") && (pcbStrArray[i].Substring(9, 18).ToUpper() == "NAME=&PLANE LAYERS"))
                                    {
                                        isLayersCounting = true;
                                    }
                                if ((isLayersCounting == true) && (i < pcbStrArray.Length - 2))
                                {
                                    if (pcbStrArray[i + 1].Split(new Char[] { '=' }).Length > 1)
                                        if (pcbStrArray[i + 1].Split(new Char[] { '=' })[1].Length > 0)
                                        {
                                            string[] layersArray = pcbStrArray[i + 1].Split(new Char[] { ',' });
                                            pcbLayersCount += layersArray.Length;
                                        }
                                    isLayersCounting = false;
                                }
                                //Определяем наименования и толщины диэлектриков

                                if (curStrLength >= 26)
                                    if ((pcbStrArray[i].Substring(0, 14).ToUpper() == "V9_STACK_LAYER") && (pcbStrArray[i].Substring(curStrLength - 10).ToUpper() == "DIELTYPE=2"))
                                    {
                                        dielLayerNumber = pcbStrArray[i].Substring(("V9_STACK_LAYER").Length);
                                        dielLayerNumber = dielLayerNumber.Substring(0, dielLayerNumber.Length - "_DIELTYPE=2".Length);
                                        pcbMaterialsItem.DielType = 2;
                                    }
                                    else if ((pcbStrArray[i].Substring(0, 14).ToUpper() == "V9_STACK_LAYER") && (pcbStrArray[i].Substring(curStrLength - 10).ToUpper() == "DIELTYPE=1"))
                                    {
                                        dielLayerNumber = pcbStrArray[i].Substring(("V9_STACK_LAYER").Length);
                                        dielLayerNumber = dielLayerNumber.Substring(0, dielLayerNumber.Length - "_DIELTYPE=1".Length);
                                        pcbMaterialsItem.DielType = 1;
                                    }

                                if (curStrLength > 27)
                                    if (pcbStrArray[i].Substring(0, 26 + dielLayerNumber.Length) == "V9_STACK_LAYER" + dielLayerNumber + "_DIELHEIGHT=")
                                    {
                                        double height = 0;
                                        string heightStr = pcbStrArray[i].Substring(26 + dielLayerNumber.Length);
                                        if (heightStr.Substring(heightStr.Length - 3) == "mil") heightStr = heightStr.Trim("mil".ToCharArray());
                                        IFormatProvider formatter = new System.Globalization.NumberFormatInfo { NumberDecimalSeparator = "." };
                                        height = Math.Round(Convert.ToDouble(heightStr, formatter) * 0.0254, 3);
                                        pcbMaterialsItem.Height = height.ToString("N3");
                                    }
                                if (curStrLength > 29)
                                    if (pcbStrArray[i].Substring(0, 28 + dielLayerNumber.Length) == "V9_STACK_LAYER" + dielLayerNumber + "_DIELMATERIAL=")
                                    {
                                        pcbMaterialsItem.Name = pcbStrArray[i].Substring(("V9_STACK_LAYER" + dielLayerNumber + "_DIELMATERIAL=").Length);
                                        pcbMaterialsItem.Quantity = 1;
                                        pcbMaterialsTempList.Add(pcbMaterialsItem);
                                        pcbMaterialsItem = new DielProperties();
                                    }
                            }
                        }

                        if (pcbLayersCount > 0) pcbLayersCount--; //Не считаем слой MultiLayer
                        if (pcbLayersCount > 2)
                        {
                            isPcbMultilayer = true;

                            ParameterItem parameterItem = new ParameterItem();
                            parameterItem.name = "isPcbMultilayer";
                            parameterItem.value = "true";
                            project.SaveParameterItem(parameterItem);

                            plataSpecItem.spSection = (int)Global.SpSections.SborEd; //Если плата многослойная, добавляем её в раздел спецификации "Сборочные единицы"

                            //Группируем одинаковые названия материалов слоёв платы, и сохраняем в pcbMaterialsList
                            foreach (DielProperties pcbMaterialTempItem in pcbMaterialsTempList)
                            {
                                if (pcbMaterialsList.Count > 0)
                                {
                                    bool listContainsItem = false;
                                    for (int i = 0; i < pcbMaterialsList.Count; i++)
                                        if ((pcbMaterialsList[i].Name == pcbMaterialTempItem.Name) && (pcbMaterialsList[i].Height == pcbMaterialTempItem.Height) && (pcbMaterialsList[i].DielType == pcbMaterialTempItem.DielType))
                                        {
                                            pcbMaterialsList[i].Quantity++;
                                            listContainsItem = true;
                                        }

                                    if (listContainsItem == false) pcbMaterialsList.Add(pcbMaterialTempItem);


                                }
                                else pcbMaterialsList.Add(pcbMaterialTempItem);

                            }
                        }
                        else
                        {
                            isPcbMultilayer = false;

                            ParameterItem parameterItem = new ParameterItem();
                            parameterItem.name = "isPcbMultilayer";
                            parameterItem.value = "false";
                            project.SaveParameterItem(parameterItem);
                            plataSpecItem.spSection = (int)Global.SpSections.Details;//если однослойная или двухслойная, добавляем плату в раздел "Детали"
                            ((TreeViewItem)(projectTreeViewItem.Items[0])).Items.RemoveAt(((TreeViewItem)(projectTreeViewItem.Items[0])).Items.Count - 1);
                        }
                        pcbReader.Close();
                    }

                prevPrevPrjStr = prevPrjStr;
                prevPrjStr = prjStr;

            }

            pcbPrjFile.Close();
            #endregion


            //Записываем в новый список названия всех свойств компонентов
            List<string> propNames = new List<string>();
            propNames.Add("<Не задано>");

            for (int i = 0; i < numberOfStrings; i++)
            {
                for (int j = 0; j < (componentsList[i]).Count; j++)
                {
                    string name = componentsList[i][j].Name;
                    if (propNames.Contains(name) == false) propNames.Add(name);
                }
            }
            propNames.Sort(); //сортировка по алфавиту для удобства нахождения в списке нужного параметра

            ImportPcbPrjWindow importPcbPrjWindow = new ImportPcbPrjWindow();

            string defaultDesignatorPropName = "Designator";
            string defaultNamePropName = "PartNumber";
            string defaultDocumPropName = "Manufacturer";
            string defaultNotePropName = "Note";

            #region Считывание наименований параметров компонентов из базы данных
            SettingsItem propNameItem = new SettingsItem();
            using (SettingsDB settingsDB = new SettingsDB()) { 
                //Чтение наименования свойства с позиционным обозначением
                if (settingsDB.GetItem("designatorPropName") == null)
                {
                    propNameItem.name = "designatorPropName";
                    propNameItem.valueString = "Designator";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("designatorPropName");
                defaultDesignatorPropName = propNameItem.valueString;

                //Чтение наименования свойства с наименованием компонента
                if (settingsDB.GetItem("namePropName") == null)
                {
                    propNameItem.name = "namePropName";
                    propNameItem.valueString = "PartNumber";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("namePropName");
                defaultNamePropName = propNameItem.valueString;

                //Чтение наименования свойства с документом на поставку компонента
                if (settingsDB.GetItem("documPropName") == null)
                {
                    propNameItem.name = "documPropName";
                    propNameItem.valueString = "Manufacturer";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("documPropName");
                defaultDocumPropName = propNameItem.valueString;

                //Чтение наименования свойства с комментарием
                if (settingsDB.GetItem("notePropName") == null)
                {
                    propNameItem.name = "notePropName";
                    propNameItem.valueString = "Note";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("notePropName");
                defaultNotePropName = propNameItem.valueString;
            }
            #endregion


            importPcbPrjWindow.designatorComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultDesignatorPropName)) importPcbPrjWindow.designatorComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultDesignatorPropName);
            else
            {
                importPcbPrjWindow.designatorComboBox.SelectedIndex = 0;
                //importPcbPrjWindow.nextButton.IsEnabled = false;
                importPcbPrjWindow.nextButton.ToolTip = "Свойства \"Поз. обозначение\" и \"Наименование\" должны быть заданы";
            }
            importPcbPrjWindow.nameComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultNamePropName)) importPcbPrjWindow.nameComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultNamePropName);
            else
            {
                importPcbPrjWindow.nameComboBox.SelectedIndex = 0;
                //importPcbPrjWindow.nextButton.IsEnabled = false;
                importPcbPrjWindow.nextButton.ToolTip = "Свойства \"Поз. обозначение\" и \"Наименование\" должны быть заданы";
            }

            importPcbPrjWindow.documComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultDocumPropName)) importPcbPrjWindow.documComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultDocumPropName);
            else importPcbPrjWindow.documComboBox.SelectedIndex = 0;
            importPcbPrjWindow.noteComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultNotePropName)) importPcbPrjWindow.noteComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultNotePropName);
            else importPcbPrjWindow.noteComboBox.SelectedIndex = 0;

            if (variantNameList.Count > 1)
            {
                importPcbPrjWindow.variantSelectionComboBox.ItemsSource = variantNameList;
                importPcbPrjWindow.variantSelectionComboBox.SelectedIndex = 0;
                importPcbPrjWindow.variantsGrid.Visibility = Visibility.Visible;
                importPcbPrjWindow.propertiesGrid.Visibility = Visibility.Hidden;
                importPcbPrjWindow.moduleDecimalNumberGrid.Visibility = Visibility.Hidden;
            }
            else
            {
                importPcbPrjWindow.variantsGrid.Visibility = Visibility.Hidden;
                importPcbPrjWindow.propertiesGrid.Visibility = Visibility.Visible;
                importPcbPrjWindow.moduleDecimalNumberGrid.Visibility = Visibility.Hidden;
            }

            importPcbPrjWindow.prjParamsVariantList = prjParamsVariantList;


            if (importPcbPrjWindow.ShowDialog() == true)
            {

                #region Заполнение списков для базы данных проекта
                createPdfMenuItem.IsEnabled = true;
                btnCreatePdf.IsEnabled = true;

                List<PerechenItem> perechenList = new List<PerechenItem>();
                List<SpecificationItem> specList = new List<SpecificationItem>();
                List<VedomostItem> vedomostList = new List<VedomostItem>();
                List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

                int numberOfValidStrings = 0;

                SpecificationItem tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А1";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " СБ";
                tempSpecItem.name = "Сборочный чертёж";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А1";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " Э3";
                tempSpecItem.name = "Схема электрическая принципиальная";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А4";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " ПЭ3";
                tempSpecItem.name = "Перечень элементов";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                plataSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text;
                plataSpecItem.name = "Плата печатная";

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " ВП";
                tempSpecItem.name = "Ведомость покупных изделий";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                /*tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "*)";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " Д33";
                tempSpecItem.name = "Данные проектирования модуля";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.note = "*) CD-диск";
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);*/

                project = new Data.ProjectDB(projectPath);

                PcbSpecificationItem tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(1, specTempSave.GetCurrent());
                tempPcbSpecItem.format = "А1";
                tempPcbSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text + " СБ";
                tempPcbSpecItem.name = "Сборочный чертёж";
                tempPcbSpecItem.quantity = String.Empty;
                tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
                pcbSpecList.Add(tempPcbSpecItem);

                tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(2, specTempSave.GetCurrent());
                tempPcbSpecItem.format = "*)";
                tempPcbSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text + " Т5М";
                tempPcbSpecItem.name = "Данные проектирования";
                tempPcbSpecItem.quantity = String.Empty;
                tempPcbSpecItem.note = "*) CD-диск";
                tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
                pcbSpecList.Add(tempPcbSpecItem);

                int index = 3;
                int coreQuantity = 0; //кол-во ядер в стеке платы
                foreach (DielProperties pcbMaterialItem in pcbMaterialsList)
                {
                    tempPcbSpecItem = new PcbSpecificationItem();
                    tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                    tempPcbSpecItem.format = String.Empty;
                    tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                    tempPcbSpecItem.oboznachenie = String.Empty;
                    string name = String.Empty;
                    if (pcbMaterialItem.DielType == 1)
                    {
                        coreQuantity += pcbMaterialItem.Quantity;
                        name = "Стеклотекстолит ";
                    }
                    else name = "Препрег ";
                    name += pcbMaterialItem.Name + " ";
                    name += pcbMaterialItem.Height + " мм";
                    tempPcbSpecItem.name = name;
                    tempPcbSpecItem.quantity = pcbMaterialItem.Quantity.ToString();
                    tempPcbSpecItem.note = String.Empty;
                    tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                    pcbSpecList.Add(tempPcbSpecItem);
                    index++;
                }

                int additionalCopperLayersCount = pcbLayersCount - coreQuantity * 2;
                if (additionalCopperLayersCount > 0)
                {
                    tempPcbSpecItem = new PcbSpecificationItem();
                    tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                    tempPcbSpecItem.format = String.Empty;
                    tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                    tempPcbSpecItem.oboznachenie = String.Empty;
                    tempPcbSpecItem.name = "Фольга медная толщиной 18 мкм";
                    tempPcbSpecItem.quantity = additionalCopperLayersCount.ToString();
                    tempPcbSpecItem.note = String.Empty;
                    tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                    pcbSpecList.Add(tempPcbSpecItem);
                }


                OsnNadpisItem osnNadpisItem = new OsnNadpisItem();


                DisplayPcbSpecValues(pcbSpecList);
                //Сохраняем спецификацию для ПП в БД, потому что она не требует сортировки
                for (int i = 0; i < pcbSpecList.Count; i++)
                {
                    PcbSpecificationItem sd = pcbSpecList[i];
                    sd.id = id.makeID(i + 1, pcbSpecTempSave.GetCurrent());
                    project.AddPcbSpecItem(sd);
                }

                osnNadpisItem.grapha = "2";
                osnNadpisItem.specificationValue = importPcbPrjWindow.prjNumberTextBox.Text;
                osnNadpisItem.perechenValue = osnNadpisItem.specificationValue + " ПЭ3";
                osnNadpisItem.vedomostValue = osnNadpisItem.specificationValue + " ВП";
                osnNadpisItem.pcbSpecificationValue = importPcbPrjWindow.pcbNumberTextBox.Text;
                project.SaveOsnNadpisItem(osnNadpisItem);

                osnNadpisItem.grapha = "25";
                osnNadpisItem.specificationValue = String.Empty;
                osnNadpisItem.perechenValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для перечня здесь указываем спецификацию
                osnNadpisItem.vedomostValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для ведомости здесь указываем спецификацию
                osnNadpisItem.pcbSpecificationValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для спецификации ПП здесь указываем спецификацию
                project.SaveOsnNadpisItem(osnNadpisItem);

                numberOfValidStrings++;
                plataSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                specList.Add(plataSpecItem);

                string designatorName = importPcbPrjWindow.designatorComboBox.SelectedItem.ToString();
                string nameName = importPcbPrjWindow.nameComboBox.SelectedItem.ToString();
                string documName = importPcbPrjWindow.documComboBox.SelectedItem.ToString();
                string noteName = importPcbPrjWindow.noteComboBox.SelectedItem.ToString();

                #region Сохранение выбранных наименований свойств комопонентов в SettingsDB
                propNameItem = new SettingsItem();
                using (var settingsDB = new SettingsDB()) { 

                    propNameItem.name = "designatorPropName";
                    propNameItem.valueString = designatorName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "namePropName";
                    propNameItem.valueString = nameName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "documPropName";
                    propNameItem.valueString = documName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "notePropName";
                    propNameItem.valueString = noteName;
                    settingsDB.SaveSettingItem(propNameItem);
                }
                #endregion

                for (int i = 0; i < numberOfStrings; i++)
                {
                    PerechenItem tempPerechen = new PerechenItem();
                    SpecificationItem tempSpecification = new SpecificationItem();
                    VedomostItem tempVedomost = new VedomostItem();
                    PcbSpecificationItem tempPcbSpecification = new PcbSpecificationItem();

                    numberOfValidStrings++;
                    tempPerechen.id = id.makeID(numberOfValidStrings, perTempSave.GetCurrent());
                    tempPerechen.designator = string.Empty;
                    tempPerechen.name = string.Empty;
                    tempPerechen.quantity = "1";

                    tempPerechen.docum = string.Empty;
                    tempPerechen.type = string.Empty;
                    tempPerechen.group = string.Empty;
                    tempPerechen.groupPlural = string.Empty;
                    tempPerechen.isNameUnderlined = false;
                    tempPerechen.note = string.Empty;

                    for (int j = 0; j < (componentsList[i]).Count; j++)
                    {
                        ComponentProperties prop;
                        try
                        {
                            prop = (componentsList[i])[j];

                            if (prop.Name == designatorName) tempPerechen.designator = prop.Text;

                            else if (prop.Name == nameName) tempPerechen.name = prop.Text;
                            else if (prop.Name == documName) tempPerechen.docum = prop.Text;
                            else if (prop.Name == noteName) tempPerechen.note = prop.Text;
                        }
                        catch
                        {
                            MessageBox.Show(j.ToString() + "/" + ((componentsList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + numberOfStrings);
                        }
                    }

                    #region В случае наличиня исполнений
                    bool isDNF = false;
                    int variantIndex = importPcbPrjWindow.variantSelectionComboBox.SelectedIndex;
                    if (variantIndex > 0)
                    {
                        string variantName = importPcbPrjWindow.variantSelectionComboBox.SelectedItem.ToString();
                        //Записываем выбранный вариант в ProjectDB
                        ParameterItem parameterItem = new ParameterItem();
                        parameterItem.name = "Variant";
                        parameterItem.value = variantName;
                        project.SaveParameterItem(parameterItem);

                        ((TreeViewItem)(projectTreeViewItem.Items[0])).Header = "Исполнение: " + variantName;

                        for (int j = 0; j < dnfVariantDesignatorsList[variantIndex - 1].Count; j++)
                            if (dnfVariantDesignatorsList[variantIndex - 1][j] == tempPerechen.designator) isDNF = true;
                        for (int j = 0; j < fittedVariantDesignatorsList[variantIndex - 1].Count; j++)
                        {
                            if (fittedVariantDesignatorsList[variantIndex - 1][j] == tempPerechen.designator)
                            {
                                for (int k = 0; k < componentsVariantList[variantIndex - 1][j].Count; k++)
                                {
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == nameName) tempPerechen.name = componentsVariantList[variantIndex - 1][j][k].Text;
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == documName) tempPerechen.docum = componentsVariantList[variantIndex - 1][j][k].Text;
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == noteName) tempPerechen.note = componentsVariantList[variantIndex - 1][j][k].Text;
                                }
                            }
                        }

                    }


                    #endregion

                    if (isDNF == false)
                    {

                        tempSpecification.id = tempPerechen.id;
                        tempSpecification.spSection = (int)Global.SpSections.Other;
                        tempSpecification.format = String.Empty;
                        tempSpecification.zona = String.Empty;
                        tempSpecification.position = String.Empty;
                        tempSpecification.oboznachenie = String.Empty;
                        tempSpecification.name = tempPerechen.name;
                        tempSpecification.quantity = "1";
                        tempSpecification.note = tempPerechen.designator;
                        tempSpecification.group = String.Empty;
                        tempSpecification.docum = tempPerechen.docum;
                        tempSpecification.designator = tempPerechen.designator;
                        tempSpecification.isNameUnderlined = false;

                        tempVedomost.id = tempPerechen.id;
                        tempVedomost.designator = tempPerechen.designator;
                        tempVedomost.name = tempPerechen.name;
                        tempVedomost.kod = String.Empty;
                        tempVedomost.docum = tempPerechen.docum;
                        tempVedomost.supplier = String.Empty;
                        tempVedomost.belongs = String.Empty;
                        tempVedomost.quantityIzdelie = "1";
                        tempVedomost.quantityComplects = String.Empty;
                        tempVedomost.quantityTotal = "1";
                        tempVedomost.note = tempPerechen.note;
                        tempVedomost.isNameUnderlined = false;



                        string group = string.Empty;
                        using (DesignatorDB designDB = new DesignatorDB()) { 
                            int descrDBLength = designDB.GetLength();

                            for (int j = 0; j < descrDBLength; j++)
                            {
                                DesignatorDescriptionItem desDescr = designDB.GetItem(j + 1);

                                if (tempPerechen.designator.Length >= 2)
                                    if ((desDescr.Designator == tempPerechen.designator.Substring(0, 1)) | (desDescr.Designator == tempPerechen.designator.Substring(0, 2)))
                                    {
                                        tempPerechen.group = desDescr.Group.Substring(0, 1).ToUpper() + desDescr.Group.Substring(1, desDescr.Group.Length - 1).ToLower();
                                        tempPerechen.groupPlural = desDescr.GroupPlural.Substring(0, 1).ToUpper() + desDescr.GroupPlural.Substring(1, desDescr.GroupPlural.Length - 1).ToLower();

                                        group = tempPerechen.group;

                                        tempSpecification.group = group;
                                        tempPcbSpecification.group = group;
                                        tempVedomost.group = group;
                                        tempVedomost.groupPlural = tempPerechen.groupPlural;
                                    }
                            }
                        }

                        tempSpecification.name = group + " " + tempSpecification.name + " " + tempSpecification.docum;

                        perechenList.Add(tempPerechen);
                        specList.Add(tempSpecification);
                        vedomostList.Add(tempVedomost);
                    }
                    else
                    {
                        numberOfValidStrings--;
                    }


                }

                //Сортировка по поз. обозначению
                List<PerechenItem> perechenListSorted = new List<PerechenItem>();
                List<SpecificationItem> specOtherListSorted = new List<SpecificationItem>();
                List<VedomostItem> vedomostListSorted = new List<VedomostItem>();

                perechenListSorted = perechenList.OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
                specOtherListSorted = specList.Where(x => x.spSection == ((int)Global.SpSections.Other)).OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
                vedomostListSorted = vedomostList.OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();

                for (int i = 0; i < specOtherListSorted.Count; i++)
                {
                    perechenListSorted[i].id = id.makeID(i + 1, perTempSave.GetCurrent());
                    specOtherListSorted[i].id = id.makeID(i + 1 + (numberOfValidStrings - specOtherListSorted.Count), specTempSave.GetCurrent());
                    vedomostListSorted[i].id = id.makeID(i + 1, vedomostTempSave.GetCurrent());
                }

                saveProjectMenuItem.IsEnabled = true;
                undoMenuItem.IsEnabled = true;
                redoMenuItem.IsEnabled = true;
                #endregion
                groupByNameSaveAndPrint(perechenListSorted, specList.Where(x => x.spSection != ((int)Global.SpSections.Other)).ToList(), specOtherListSorted, vedomostListSorted);

                //waitGrid.Visibility = Visibility.Hidden;
            }

        }

        private void ImportPrjfromKiCad_Click(object sender, RoutedEventArgs e)
        {
            waitMessageLabel.Content = "Подождите, импорт данных...";
            waitGrid.Visibility = Visibility.Visible;

            OpenFileDialog openDlg = new OpenFileDialog();
            openDlg.Title = "Выбор файла проекта KiCad";
            openDlg.Multiselect = false;
            openDlg.Filter = "Файлы проекта KiCad (*.kicad_pro)|*.kicad_pro";
            if (openDlg.ShowDialog() == true)
            {
                string pcbPrjFilePath = openDlg.FileName;

                ImportPrjfromKiCad(pcbPrjFilePath);
                waitGrid.Visibility = Visibility.Hidden;
            }
            else
            {
                waitGrid.Visibility = Visibility.Hidden;
            }
        }



        /// <summary> Импорт данных из проекта KiCad </summary>
        private void ImportPrjfromKiCad(string pcbPrjFilePath)
        {
            perTempSave = new TempSaves();
            specTempSave = new TempSaves();
            vedomostTempSave = new TempSaves();

            List<DielProperties> pcbMaterialsList = new List<DielProperties>();

            #region Открытие и парсинг файла проекта KiCad

            string pcbPrjFolderPath = System.IO.Path.GetDirectoryName(pcbPrjFilePath);

            //Открываем файл проекта KiCAD для чтения текстовых переменных проекта:
            FileStream pcbPrjFile = new FileStream(pcbPrjFilePath, FileMode.Open, FileAccess.Read);
            StreamReader pcbPrjReader = new StreamReader(pcbPrjFile, System.Text.Encoding.UTF8);

            String prjStr = String.Empty;
            String prevPrjStr = String.Empty;
            String prevPrevPrjStr = String.Empty;

            int numberOfStrings = 0;

            List<List<ComponentProperties>> componentsList = new List<List<ComponentProperties>>();
            List<ComponentProperties> componentPropList = new List<ComponentProperties>();

            List<List<List<ComponentProperties>>> componentsVariantList = new List<List<List<ComponentProperties>>>();
            List<List<ComponentProperties>> componentsPropVariantList = new List<List<ComponentProperties>>();
            List<ComponentProperties> componentVariantPropList = new List<ComponentProperties>();
            List<String> variantNameList = new List<String>();
            List<String> dnfDesignatorsList = new List<String>();
            List<List<String>> dnfVariantDesignatorsList = new List<List<String>>();
            List<String> fittedDesignatorsList = new List<String>();
            List<List<String>> fittedVariantDesignatorsList = new List<List<String>>();

            List<List<string[]>> prjParamsVariantList = new List<List<string[]>>();


            variantNameList.Add("No Variations");
            //bool isWaitingVariantDescription = false;

            List<ComponentProperties> otherPropList = new List<ComponentProperties>();

            SpecificationItem plataSpecItem = new SpecificationItem(); //для хранения записи печатной платы для добавления в раздел "Детали" или "Сборочные единицы" спецификации

            plataSpecItem.position = SpecificationItem.POS_AUTO;
            plataSpecItem.name = "Плата печатная";
            plataSpecItem.quantity = "1";
            int pcbLayersCount = 0;

            //int currentVariant = 0;

            bool isReadingTextVariables = false;

            while ((prjStr = pcbPrjReader.ReadLine()) != null)
            {
                #region Запись данных об исполнениях в список - пока не реализовано в KiCad


                #endregion



                if (prjStr.Contains("\"text_variables\": {"))
                {
                    isReadingTextVariables = true;
                }
                else if (isReadingTextVariables == true)
                {
                    if (prjStr.Replace(" ", String.Empty) == "}") isReadingTextVariables = false;
                    else
                    {
                        prjStr = prjStr.Replace("\",", String.Empty);
                        prjStr = prjStr.Replace("\"", String.Empty);
                        string[] paramPrjStrArray = prjStr.Split(new string[] { ": " }, StringSplitOptions.None);
                        int variantNumber = 0;
                        //if (paramPrjStrArray.Length > 1) variantNumber = currentVariant;


                        string[] nameValue = new string[2];

                        nameValue[0] = paramPrjStrArray[0].Trim();
                        nameValue[1] = paramPrjStrArray[1].Trim();

                        while (prjParamsVariantList.Count <= variantNumber) prjParamsVariantList.Add(new List<string[]>());


                        prjParamsVariantList[variantNumber].Add(nameValue);
                    }
                }
            }


            string schPath = System.IO.Path.GetDirectoryName(pcbPrjFilePath) + @"\" + System.IO.Path.GetFileNameWithoutExtension(pcbPrjFilePath) + ".kicad_sch";


            //Открываем файл схемы KiCad для получения списка параметров электронных компонентов:
            FileStream schFile = new FileStream(schPath, FileMode.Open, FileAccess.Read);
            StreamReader schReader = new StreamReader(schFile, System.Text.Encoding.UTF8);

            string schStr = String.Empty;

            bool isNoBom = false;
            bool isFirstPartOfComponent = true;
            bool isComponent = false;


            while ((schStr = schReader.ReadLine()) != null)
            {
                StreamReader subsSchReader = new StreamReader(schFile, System.Text.Encoding.UTF8); ;
                int numOfSchStr = 0;
                //Открываем последующие старницы схемы, если на первой странице встречаем ссылку на неё:
                if (schStr.Trim().Length > 21)
                    if (schStr.Trim().Substring(0, 21) == "(property \"Sheetfile\"") //если схема многолистовая, открываем последующие листы
                    {
                        string subsSchPath = String.Empty;
                        subsSchPath = schStr.Trim().Substring(23, schStr.Trim().Length-24);
                        if (schStr.Trim().Contains(":/") == false) //если относительный путь
                            subsSchPath = System.IO.Path.GetDirectoryName(pcbPrjFilePath) + "/" + subsSchPath;
                        subsSchPath=subsSchPath.Replace('/','\\');
                        
                        //Открываем файл схемы KiCad для получения списка параметров электронных компонентов:
                        FileStream subsSchFile = new FileStream(subsSchPath, FileMode.Open, FileAccess.Read);
                        subsSchReader = new StreamReader(subsSchFile, System.Text.Encoding.UTF8);
                        while ((schStr = subsSchReader.ReadLine()) != null) numOfSchStr++;
                        subsSchReader.Close();
                        subsSchFile = new FileStream(subsSchPath, FileMode.Open, FileAccess.Read);
                        subsSchReader = new StreamReader(subsSchFile, System.Text.Encoding.UTF8);
                    }
                if (numOfSchStr == 0) // т.е. если считываем первую страницу схемы
                    numOfSchStr = 1; // идём по порядку, т.е повторяем последующий код только один раз для текущей строки                    

                for (int i = 0; i < numOfSchStr; i++)
                {
                    if (numOfSchStr > 1) schStr = subsSchReader.ReadLine();//если работаем не с первой страницей проекта, считываем очередную строку
                        
                    
                    if (isComponent == true)
                    {
                        if (schStr.Trim() == "(in_bom yes)") isNoBom = false;
                        else if (schStr.Trim() == "(in_bom no)") isNoBom = true;

                        if (schStr.Trim() == "(dnp yes)") isNoBom = true;

                        if (schStr.Trim().Length > 7)
                            if (schStr.Trim().Substring(0, 6) == "(unit ")
                                if (schStr.Trim() != "(unit 1)") // Если многосекционный компонент, берём параметры только из первого его УГО
                                    isNoBom = true;


                        /*if (schStrArray[i].Length >= 15)
                                if (schStrArray[i].Substring(0, 14).ToUpper() == "CURRENTPARTID=")
                                    if ((schStrArray[i].Substring(0, 15).ToUpper() != "CURRENTPARTID=1") | (schStrArray[i].Length > 15)) isFirstPartOfComponent = false;*/

                        if (schStr.Trim().Length > 11)
                            if (schStr.Trim().Substring(0, 11).ToLower() == "(property \"")
                            {
                                ComponentProperties prop = new ComponentProperties();
                                schStr = schStr.Trim().Substring(11);

                                prop.Name = schStr.Split(new string[] { "\" \"" }, StringSplitOptions.None)[0];
                                prop.Text = schStr.Split(new string[] { "\" \"" }, StringSplitOptions.None)[1].Trim(new char[] { '"' });
                                if (prop.Text.Length >= 1)
                                    if ((prop.Name == "Reference") && (prop.Text.Substring(0, 1) == "#")) isNoBom = true; // для символов Земли и питания     
                                if (isNoBom == false)
                                {
                                    componentPropList.Add(prop);
                                }

                            }

                        if ((schStr.Trim() == "(symbol") | (schStr.Trim() == "(sheet_instances") | (schStr== "(kicad_sch")) //Считаем, что описание каждого компонента заканчивается этой фразой
                        {
                            if ((isNoBom == false) && (componentPropList.Count > 0) && (isFirstPartOfComponent == true))
                            {
                                componentsList.Add(componentPropList);

                                numberOfStrings++;
                            }

                            isNoBom = false;
                            if ((schStr.Trim() != "(symbol")) isComponent = false;
                            isFirstPartOfComponent = true;

                            componentPropList = new List<ComponentProperties>();
                        }
                    }

                    //Теперь ищем все записи компонента и сохраняем их
                    if (schStr.Trim() == "(symbol") //Считаем, что описание каждого компонента начинается этой фразой                        
                        isComponent = true;
                }



            }


            schFile.Close();


            string pcbPath = System.IO.Path.GetDirectoryName(pcbPrjFilePath) + @"\" + System.IO.Path.GetFileNameWithoutExtension(pcbPrjFilePath) + ".kicad_pcb";

            //Открываем файл платы KiCad для получения количества слоёв платы:
            FileStream pcbFile = new FileStream(pcbPath, FileMode.Open, FileAccess.Read);
            StreamReader pcbReader = new StreamReader(pcbFile, System.Text.Encoding.UTF8);

            string pcbStr = String.Empty;

            //Считываем свойства платы

            List<DielProperties> pcbMaterialsTempList = new List<DielProperties>();
            DielProperties pcbMaterialsItem = new DielProperties();

            while ((pcbStr = pcbReader.ReadLine()) != null)
            {


                //Определяем количество слоёв платы, и определяем, к какому разделу спецификации будет отноститься плата
                if (pcbStr.Trim() == "(type \"copper\")") pcbLayersCount++;


                //Определяем наименования и толщины диэлектриков

                if (pcbStr.Trim() == "(type \"prepreg\")") pcbMaterialsItem.DielType = 2;
                if (pcbStr.Trim() == "(type \"core\")") pcbMaterialsItem.DielType = 1;
                if (pcbStr.Trim().Length > 11)
                {
                    if (pcbStr.Trim().Substring(0, 10) == "(thickness") pcbMaterialsItem.Height = pcbStr.Trim().Substring(11).Trim(new char[] { ')' }).Replace('.', ',');
                    if (pcbStr.Trim().Substring(0, 9) == "(material")
                    {
                        pcbMaterialsItem.Name = pcbStr.Trim().Substring(11).Trim(new char[] { '\"', ')' });
                        pcbMaterialsItem.Quantity = 1;
                        pcbMaterialsTempList.Add(pcbMaterialsItem);
                        pcbMaterialsItem = new DielProperties();
                    }
                }
            }

            if (pcbLayersCount > 2)
            {
                isPcbMultilayer = true;

                ParameterItem parameterItem = new ParameterItem();
                parameterItem.name = "isPcbMultilayer";
                parameterItem.value = "true";
                project.SaveParameterItem(parameterItem);

                plataSpecItem.spSection = (int)Global.SpSections.SborEd; //Если плата многослойная, добавляем её в раздел спецификации "Сборочные единицы"

                //Группируем одинаковые названия материалов слоёв платы, и сохраняем в pcbMaterialsList
                foreach (DielProperties pcbMaterialTempItem in pcbMaterialsTempList)
                {
                    if (pcbMaterialsList.Count > 0)
                    {
                        bool listContainsItem = false;
                        for (int i = 0; i < pcbMaterialsList.Count; i++)
                            if ((pcbMaterialsList[i].Name == pcbMaterialTempItem.Name) && (pcbMaterialsList[i].Height == pcbMaterialTempItem.Height) && (pcbMaterialsList[i].DielType == pcbMaterialTempItem.DielType))
                            {
                                pcbMaterialsList[i].Quantity++;
                                listContainsItem = true;
                            }

                        if (listContainsItem == false) pcbMaterialsList.Add(pcbMaterialTempItem);


                    }
                    else pcbMaterialsList.Add(pcbMaterialTempItem);

                }
            }
            else
            {
                isPcbMultilayer = false;

                ParameterItem parameterItem = new ParameterItem();
                parameterItem.name = "isPcbMultilayer";
                parameterItem.value = "false";
                project.SaveParameterItem(parameterItem);
                plataSpecItem.spSection = (int)Global.SpSections.Details;//если однослойная или двухслойная, добавляем плату в раздел "Детали"
                if (((TreeViewItem)(projectTreeViewItem.Items[0])).Items.Count == 4) //если есть спецификация печатной платы, удаляем её
                ((TreeViewItem)(projectTreeViewItem.Items[0])).Items.RemoveAt(((TreeViewItem)(projectTreeViewItem.Items[0])).Items.Count - 1);
            }
            pcbReader.Close();


            pcbPrjFile.Close();
            #endregion


            //Записываем в новый список названия всех свойств компонентов
            List<string> propNames = new List<string>();
            propNames.Add("<Не задано>");

            for (int i = 0; i < numberOfStrings; i++)
            {
                for (int j = 0; j < (componentsList[i]).Count; j++)
                {
                    string name = componentsList[i][j].Name;
                    if (propNames.Contains(name) == false) propNames.Add(name);
                }
            }
            propNames.Sort(); //сортировка по алфавиту для удобства нахождения в списке нужного параметра

            ImportPcbPrjWindow importPcbPrjWindow = new ImportPcbPrjWindow();

            string defaultDesignatorPropName = "Designator";
            string defaultNamePropName = "PartNumber";
            string defaultDocumPropName = "Manufacturer";
            string defaultNotePropName = "Note";

            #region Считывание наименований параметров компонентов из базы данных
            SettingsItem propNameItem = new SettingsItem();
            using (SettingsDB settingsDB = new SettingsDB()) { 
                //Чтение наименования свойства с позиционным обозначением
                if (settingsDB.GetItem("designatorPropName") == null)
                {
                    propNameItem.name = "designatorPropName";
                    propNameItem.valueString = "Designator";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("designatorPropName");
                defaultDesignatorPropName = propNameItem.valueString;

                //Чтение наименования свойства с наименованием компонента
                if (settingsDB.GetItem("namePropName") == null)
                {
                    propNameItem.name = "namePropName";
                    propNameItem.valueString = "PartNumber";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("namePropName");
                defaultNamePropName = propNameItem.valueString;

                //Чтение наименования свойства с документом на поставку компонента
                if (settingsDB.GetItem("documPropName") == null)
                {
                    propNameItem.name = "documPropName";
                    propNameItem.valueString = "Manufacturer";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("documPropName");
                defaultDocumPropName = propNameItem.valueString;

                //Чтение наименования свойства с комментарием
                if (settingsDB.GetItem("notePropName") == null)
                {
                    propNameItem.name = "notePropName";
                    propNameItem.valueString = "Note";
                    settingsDB.SaveSettingItem(propNameItem);
                }
                else propNameItem = settingsDB.GetItem("notePropName");
                defaultNotePropName = propNameItem.valueString;
            }
            #endregion


            importPcbPrjWindow.designatorComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultDesignatorPropName)) importPcbPrjWindow.designatorComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultDesignatorPropName);
            else
            {
                importPcbPrjWindow.designatorComboBox.SelectedIndex = 0;
                //importPcbPrjWindow.nextButton.IsEnabled = false;
                importPcbPrjWindow.nextButton.ToolTip = "Свойства \"Поз. обозначение\" и \"Наименование\" должны быть заданы";
            }
            importPcbPrjWindow.nameComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultNamePropName)) importPcbPrjWindow.nameComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultNamePropName);
            else
            {
                importPcbPrjWindow.nameComboBox.SelectedIndex = 0;
                //importPcbPrjWindow.nextButton.IsEnabled = false;
                importPcbPrjWindow.nextButton.ToolTip = "Свойства \"Поз. обозначение\" и \"Наименование\" должны быть заданы";
            }

            importPcbPrjWindow.documComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultDocumPropName)) importPcbPrjWindow.documComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultDocumPropName);
            else importPcbPrjWindow.documComboBox.SelectedIndex = 0;
            importPcbPrjWindow.noteComboBox.ItemsSource = propNames;
            if (propNames.Contains(defaultNotePropName)) importPcbPrjWindow.noteComboBox.SelectedIndex = propNames.FindIndex(x => x == defaultNotePropName);
            else importPcbPrjWindow.noteComboBox.SelectedIndex = 0;

            if (variantNameList.Count > 1)
            {
                importPcbPrjWindow.variantSelectionComboBox.ItemsSource = variantNameList;
                importPcbPrjWindow.variantSelectionComboBox.SelectedIndex = 0;
                importPcbPrjWindow.variantsGrid.Visibility = Visibility.Visible;
                importPcbPrjWindow.propertiesGrid.Visibility = Visibility.Hidden;
                importPcbPrjWindow.moduleDecimalNumberGrid.Visibility = Visibility.Hidden;
            }
            else
            {
                importPcbPrjWindow.variantsGrid.Visibility = Visibility.Hidden;
                importPcbPrjWindow.propertiesGrid.Visibility = Visibility.Visible;
                importPcbPrjWindow.moduleDecimalNumberGrid.Visibility = Visibility.Hidden;
            }

            importPcbPrjWindow.prjParamsVariantList = prjParamsVariantList;


            if (importPcbPrjWindow.ShowDialog() == true)
            {

                #region Заполнение списков для базы данных проекта
                createPdfMenuItem.IsEnabled = true;
                btnCreatePdf.IsEnabled = true;

                List<PerechenItem> perechenList = new List<PerechenItem>();
                List<SpecificationItem> specList = new List<SpecificationItem>();
                List<VedomostItem> vedomostList = new List<VedomostItem>();
                List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

                int numberOfValidStrings = 0;

                SpecificationItem tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text.ToString() + " СБ";
                tempSpecItem.name = "Сборочный чертёж";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text.ToString() + " Э3";
                tempSpecItem.name = "Схема электрическая принципиальная";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А4";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " ПЭ3";
                tempSpecItem.name = "Перечень элементов";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                plataSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text;
                plataSpecItem.name = "Плата печатная";

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " ВП";
                tempSpecItem.name = "Ведомость покупных изделий";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                /*tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "*)";
                tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " Д33";
                tempSpecItem.name = "Данные проектирования модуля";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.note = "*) CD-диск";
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);*/

                project = new Data.ProjectDB(projectPath);

                PcbSpecificationItem tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(1, specTempSave.GetCurrent());
                tempPcbSpecItem.format = "А1";
                tempPcbSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text + " СБ";
                tempPcbSpecItem.name = "Сборочный чертёж";
                tempPcbSpecItem.quantity = String.Empty;
                tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
                pcbSpecList.Add(tempPcbSpecItem);

                tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(2, specTempSave.GetCurrent());
                tempPcbSpecItem.format = "*)";
                tempPcbSpecItem.oboznachenie = importPcbPrjWindow.pcbNumberTextBox.Text + " Т5М";
                tempPcbSpecItem.name = "Данные проектирования";
                tempPcbSpecItem.quantity = String.Empty;
                tempPcbSpecItem.note = "*) CD-диск";
                tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
                pcbSpecList.Add(tempPcbSpecItem);

                int index = 3;
                int coreQuantity = 0; //кол-во ядер в стеке платы
                foreach (DielProperties pcbMaterialItem in pcbMaterialsList)
                {
                    tempPcbSpecItem = new PcbSpecificationItem();
                    tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                    tempPcbSpecItem.format = String.Empty;
                    tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                    tempPcbSpecItem.oboznachenie = String.Empty;
                    string name = String.Empty;
                    if (pcbMaterialItem.DielType == 1)
                    {
                        coreQuantity += pcbMaterialItem.Quantity;
                        name = "Стеклотекстолит ";
                    }
                    else name = "Препрег ";
                    name += pcbMaterialItem.Name + " ";
                    name += pcbMaterialItem.Height + " мм";
                    tempPcbSpecItem.name = name;
                    tempPcbSpecItem.quantity = pcbMaterialItem.Quantity.ToString();
                    tempPcbSpecItem.note = String.Empty;
                    tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                    pcbSpecList.Add(tempPcbSpecItem);
                    index++;
                }

                int additionalCopperLayersCount = pcbLayersCount - coreQuantity * 2;
                if (additionalCopperLayersCount > 0)
                {
                    tempPcbSpecItem = new PcbSpecificationItem();
                    tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                    tempPcbSpecItem.format = String.Empty;
                    tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                    tempPcbSpecItem.oboznachenie = String.Empty;
                    tempPcbSpecItem.name = "Фольга медная толщиной 18 мкм";
                    tempPcbSpecItem.quantity = additionalCopperLayersCount.ToString();
                    tempPcbSpecItem.note = String.Empty;
                    tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                    pcbSpecList.Add(tempPcbSpecItem);
                }


                OsnNadpisItem osnNadpisItem = new OsnNadpisItem();


                DisplayPcbSpecValues(pcbSpecList);
                //Сохраняем спецификацию для ПП в БД, потому что она не требует сортировки
                for (int i = 0; i < pcbSpecList.Count; i++)
                {
                    PcbSpecificationItem sd = pcbSpecList[i];
                    sd.id = id.makeID(i + 1, pcbSpecTempSave.GetCurrent());
                    project.AddPcbSpecItem(sd);
                }

                osnNadpisItem.grapha = "2";
                osnNadpisItem.specificationValue = importPcbPrjWindow.prjNumberTextBox.Text;
                osnNadpisItem.perechenValue = osnNadpisItem.specificationValue + " ПЭ3";
                osnNadpisItem.vedomostValue = osnNadpisItem.specificationValue + " ВП";
                osnNadpisItem.pcbSpecificationValue = importPcbPrjWindow.pcbNumberTextBox.Text;
                project.SaveOsnNadpisItem(osnNadpisItem);

                osnNadpisItem.grapha = "25";
                osnNadpisItem.specificationValue = String.Empty;
                osnNadpisItem.perechenValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для перечня здесь указываем спецификацию
                osnNadpisItem.vedomostValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для ведомости здесь указываем спецификацию
                osnNadpisItem.pcbSpecificationValue = importPcbPrjWindow.prjNumberTextBox.Text; //Для спецификации ПП здесь указываем спецификацию
                project.SaveOsnNadpisItem(osnNadpisItem);

                numberOfValidStrings++;
                plataSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                specList.Add(plataSpecItem);

                string designatorName = importPcbPrjWindow.designatorComboBox.SelectedItem.ToString();
                string nameName = importPcbPrjWindow.nameComboBox.SelectedItem.ToString();
                string documName = importPcbPrjWindow.documComboBox.SelectedItem.ToString();
                string noteName = importPcbPrjWindow.noteComboBox.SelectedItem.ToString();

                #region Сохранение выбранных наименований свойств комопонентов в SettingsDB
                propNameItem = new SettingsItem();
                using (var settingsDB = new SettingsDB()) { 

                    propNameItem.name = "designatorPropName";
                    propNameItem.valueString = designatorName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "namePropName";
                    propNameItem.valueString = nameName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "documPropName";
                    propNameItem.valueString = documName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "notePropName";
                    propNameItem.valueString = noteName;
                    settingsDB.SaveSettingItem(propNameItem);
                }
                #endregion

                for (int i = 0; i < numberOfStrings; i++)
                {
                    PerechenItem tempPerechen = new PerechenItem();
                    SpecificationItem tempSpecification = new SpecificationItem();
                    VedomostItem tempVedomost = new VedomostItem();
                    PcbSpecificationItem tempPcbSpecification = new PcbSpecificationItem();

                    numberOfValidStrings++;
                    tempPerechen.id = id.makeID(numberOfValidStrings, perTempSave.GetCurrent());
                    tempPerechen.designator = string.Empty;
                    tempPerechen.name = string.Empty;
                    tempPerechen.quantity = "1";

                    tempPerechen.docum = string.Empty;
                    tempPerechen.type = string.Empty;
                    tempPerechen.group = string.Empty;
                    tempPerechen.groupPlural = string.Empty;
                    tempPerechen.isNameUnderlined = false;
                    tempPerechen.note = string.Empty;

                    for (int j = 0; j < (componentsList[i]).Count; j++)
                    {
                        ComponentProperties prop;
                        try
                        {
                            prop = (componentsList[i])[j];

                            if (prop.Name == designatorName) tempPerechen.designator = prop.Text;

                            else if (prop.Name == nameName) tempPerechen.name = prop.Text;
                            else if (prop.Name == documName) tempPerechen.docum = prop.Text;
                            else if (prop.Name == noteName) tempPerechen.note = prop.Text;
                        }
                        catch
                        {
                            MessageBox.Show(j.ToString() + "/" + ((componentsList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + numberOfStrings);
                        }
                    }

                    #region В случае наличиня исполнений

                    bool isDNF = false;
                    /*
                    int variantIndex = importPcbPrjWindow.variantSelectionComboBox.SelectedIndex;
                    if (variantIndex > 0)
                    {
                        string variantName = importPcbPrjWindow.variantSelectionComboBox.SelectedItem.ToString();
                        //Записываем выбранный вариант в ProjectDB
                        ParameterItem parameterItem = new ParameterItem();
                        parameterItem.name = "Variant";
                        parameterItem.value = variantName;
                        project.SaveParameterItem(parameterItem);

                        ((TreeViewItem)(projectTreeViewItem.Items[0])).Header = "Исполнение: " + variantName;

                        for (int j = 0; j < dnfVariantDesignatorsList[variantIndex - 1].Count; j++)
                            if (dnfVariantDesignatorsList[variantIndex - 1][j] == tempPerechen.designator) isDNF = true;
                        for (int j = 0; j < fittedVariantDesignatorsList[variantIndex - 1].Count; j++)
                        {
                            if (fittedVariantDesignatorsList[variantIndex - 1][j] == tempPerechen.designator)
                            {
                                for (int k = 0; k < componentsVariantList[variantIndex - 1][j].Count; k++)
                                {
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == nameName) tempPerechen.name = componentsVariantList[variantIndex - 1][j][k].Text;
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == documName) tempPerechen.docum = componentsVariantList[variantIndex - 1][j][k].Text;
                                    if (componentsVariantList[variantIndex - 1][j][k].Name == noteName) tempPerechen.note = componentsVariantList[variantIndex - 1][j][k].Text;
                                }
                            }
                        }

                    }
                    */

                    #endregion
                    

                    if (isDNF == false)
                    {

                        tempSpecification.id = tempPerechen.id;
                        tempSpecification.spSection = (int)Global.SpSections.Other;
                        tempSpecification.format = String.Empty;
                        tempSpecification.zona = String.Empty;
                        tempSpecification.position = String.Empty;
                        tempSpecification.oboznachenie = String.Empty;
                        tempSpecification.name = tempPerechen.name;
                        tempSpecification.quantity = "1";
                        tempSpecification.note = tempPerechen.designator;
                        tempSpecification.group = String.Empty;
                        tempSpecification.docum = tempPerechen.docum;
                        tempSpecification.designator = tempPerechen.designator;
                        tempSpecification.isNameUnderlined = false;

                        tempVedomost.id = tempPerechen.id;
                        tempVedomost.designator = tempPerechen.designator;
                        tempVedomost.name = tempPerechen.name;
                        tempVedomost.kod = String.Empty;
                        tempVedomost.docum = tempPerechen.docum;
                        tempVedomost.supplier = String.Empty;
                        tempVedomost.belongs = String.Empty;
                        tempVedomost.quantityIzdelie = "1";
                        tempVedomost.quantityComplects = String.Empty;
                        tempVedomost.quantityTotal = "1";
                        tempVedomost.note = tempPerechen.note;
                        tempVedomost.isNameUnderlined = false;



                        string group = string.Empty;
                        using (DesignatorDB designDB = new DesignatorDB()) { 
                            int descrDBLength = designDB.GetLength();

                            for (int j = 0; j < descrDBLength; j++)
                            {
                                DesignatorDescriptionItem desDescr = designDB.GetItem(j + 1);

                                if (tempPerechen.designator.Length >= 2)
                                    if ((desDescr.Designator == tempPerechen.designator.Substring(0, 1)) | (desDescr.Designator == tempPerechen.designator.Substring(0, 2)))
                                    {
                                        tempPerechen.group = desDescr.Group.Substring(0, 1).ToUpper() + desDescr.Group.Substring(1, desDescr.Group.Length - 1).ToLower();
                                        tempPerechen.groupPlural = desDescr.GroupPlural.Substring(0, 1).ToUpper() + desDescr.GroupPlural.Substring(1, desDescr.GroupPlural.Length - 1).ToLower();

                                        group = tempPerechen.group;

                                        tempSpecification.group = group;
                                        tempPcbSpecification.group = group;
                                        tempVedomost.group = group;
                                        tempVedomost.groupPlural = tempPerechen.groupPlural;
                                    }
                            }
                        }


                        tempSpecification.name = group + " " + tempSpecification.name + " " + tempSpecification.docum;

                        perechenList.Add(tempPerechen);
                        specList.Add(tempSpecification);
                        vedomostList.Add(tempVedomost);
                    }
                    else
                    {
                        numberOfValidStrings--;
                    }


                }

                //Сортировка по поз. обозначению
                List<PerechenItem> perechenListSorted = new List<PerechenItem>();
                List<SpecificationItem> specOtherListSorted = new List<SpecificationItem>();
                List<VedomostItem> vedomostListSorted = new List<VedomostItem>();

                perechenListSorted = perechenList.OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
                specOtherListSorted = specList.Where(x => x.spSection == ((int)Global.SpSections.Other)).OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();
                vedomostListSorted = vedomostList.OrderBy(x => MakeDesignatorForOrdering(x.designator)).ToList();

                for (int i = 0; i < specOtherListSorted.Count; i++)
                {
                    perechenListSorted[i].id = id.makeID(i + 1, perTempSave.GetCurrent());
                    specOtherListSorted[i].id = id.makeID(i + 1 + (numberOfValidStrings - specOtherListSorted.Count), specTempSave.GetCurrent());
                    vedomostListSorted[i].id = id.makeID(i + 1, vedomostTempSave.GetCurrent());
                }

                saveProjectMenuItem.IsEnabled = true;
                undoMenuItem.IsEnabled = true;
                redoMenuItem.IsEnabled = true;
                #endregion
                groupByNameSaveAndPrint(perechenListSorted, specList.Where(x => x.spSection != ((int)Global.SpSections.Other)).ToList(), specOtherListSorted, vedomostListSorted);

                //waitGrid.Visibility = Visibility.Hidden;
            }

        }

        void ShowError(string text) {
            MessageBox.Show(text, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        OpenFileDialog ImportPrjfromMentor_openDlg;
        public void CommandBinding_ImportMentorCsv(object sender, ExecutedRoutedEventArgs e) { 
            if (ImportPrjfromMentor_openDlg == null) { 
                ImportPrjfromMentor_openDlg = new OpenFileDialog() {
                    Title = "Выбор CSV-файла, экспортированного из Mentor",
                    Multiselect = false,
                    Filter = "Mentor export file (*.txt)|*.txt" + "|" + "Mentor export file (*.csv)|*.csv"
                };
            }
            else
                ImportPrjfromMentor_openDlg.FileName = ImportPrjfromMentor_openDlg.SafeFileName;

            waitMessageLabel.Content = "Подождите, импорт данных...";
            waitGrid.Visibility = Visibility.Visible;
            try { 
                if (ImportPrjfromMentor_openDlg.ShowDialog() == false) return;
                ImportPrjfromMentor(ImportPrjfromMentor_openDlg.FileName, "", "");
            } finally {
                waitGrid.Visibility = Visibility.Hidden;
            }
        }

        void ImportPrjfromMentor(string pcbPrjFile, string SignDate, string OrgName) {
            try {
                string fn;
                fn = Path.ChangeExtension(pcbPrjFile, ".docGOST");
                CreateProject(fn);
                ImportPrjfromMentorCsv(pcbPrjFile, SignDate, OrgName);
            } catch (Exception ex) {
                ShowError(ex.Message);
            }
        }

        public struct CompProperties {
            public enum Name
            {
                designator, name, docum, manuf, aux, section, shifr, qty, format, pos
            }
            public Name name;
            public string text;
            public CompProperties(Name name, string text) {
                this.name = name;
                this.text = text;
            }
        }

        private void ImportPrjfromMentorCsv(string pcbPrjFile, string SignDate, string OrgName) {
            const int HDR_LINE_CNT = 13;
            const int HDR_PARENT_SHIFR = 0; // индекс строки (0+) в хедере CSV-файла, в которой располагается "Дец. номер схемы"
            const int HDR_IDX_SCH_NAME = 2; // Имя схемы
            const int HDR_IDX_SHIFR = 4;
            const int HDR_IDX_ISPOLNITEL = 6; // Исполнитель
            const int HDR_IDX_PROVERIL = 7; // Проверил
            const int HDR_IDX_UTVERDIL = 8; // Утвердил
            const int HDR_IDX_NORMOKONTROL = 9; // Нормоконтроль

            const int LINE_PARAM_CNT = 9;
            const int LINE_PIDX_N = 0; // индекс колонки (0+) внутри строки основной части CSV-файла, в которой располагается порядковый номер
            const int LINE_PIDX_PART_NUMBER = 1;
            const int LINE_PIDX_VALUE = 2;
            const int LINE_PIDX_QTY = 3;
            const int LINE_PIDX_REFDES = 4;
            const int LINE_PIDX_MANUFACTURER = 5;
            const int LINE_PIDX_PN = 6;
            const int LINE_PIDX_TU = 7;
            const int LINE_PIDX_AUX = 8;

            const string FLAG_NO_VPI = "{{novpi}}";
            const string FLAG_DEC_N = "{{DecN}}";

            var subst_map = new Dictionary<string, (string pn, string tu, string manuf)>();
            var report = new List<string>();

            var Spec_StrToSection = new Dictionary<string, Global.SpSections>() {
                { "DET", SpSections.Details },
                { "MAT", SpSections.Materials }, 
                { "STD", SpSections.Standard },
                { "DOC", SpSections.Documentation },
                { "OTH", SpSections.Other }
            };

            string[] LoadFileHeader(CsvReader dataFile) {
                string[] rslt = new string[HDR_LINE_CNT];
                for (int i = 0; i < HDR_LINE_CNT; i++) {
                    var s = dataFile.ReadLine();
                    var pos = s.IndexOf(':');
                    if (pos == -1)
                        throw new Exception("Не найден символ ':'");
                    rslt[i] = s.Substring(pos + 1).Trim();
                }
                return rslt;
            }

            string ReplaceText(string text, string OldValue, string NewValue, ref bool bFlagPresent) {
                var pos = text.IndexOf(OldValue);
                if (pos >= 0) {
                    bFlagPresent = true;
                    return text.Substring(0, pos) + NewValue + text.Substring(pos + OldValue.Length);
                } 
                else
                    return text;
            }

            (string[] hdr, List<List<CompProperties>> comps, List<List<CompProperties>> others) LoadOneFile(string fn, int nTimes, bool bCompsNOthers) {
                List<List<CompProperties>> rslt_list = new List<List<CompProperties>>();
                List<List<CompProperties>> oth_list = new List<List<CompProperties>>();
                string[] hdr_info = null;
                using (var dataFile = new CsvReader(fn, '\t')) {
                    try {
                        if (bCompsNOthers)
                            hdr_info = LoadFileHeader(dataFile);
                        string[] SA = dataFile.ReadLineAsStrings(LINE_PARAM_CNT); // table header
                        CommonProc.RaiseIfTrue(SA[LINE_PIDX_N] != "#", "Ожидается символ '#' в 1-ой колонке");

                        while (!dataFile.Eof()) {
                            SA = dataFile.ReadLineAsStrings(LINE_PARAM_CNT);

                            if (bCompsNOthers) {
                                SA[LINE_PIDX_N].ToInt32(1, 0x7FFFFFFF); // check
                                if (SA[LINE_PIDX_VALUE] == "__FILE__") {
                                    string fn2 = SA[LINE_PIDX_PART_NUMBER];
                                    var r = LoadOneFile(Path.Combine(Path.GetDirectoryName(fn), fn2), SA[LINE_PIDX_QTY].ToInt32(1, 999), bCompsNOthers: true);
                                    rslt_list.AddRange(r.comps);
                                    oth_list.AddRange(r.others);
                                    continue;
                                }
                                SA[LINE_PIDX_QTY].ToInt32(1, 1);
                                string partNumber = SA[LINE_PIDX_PN] != "" ? SA[LINE_PIDX_PN] : SA[LINE_PIDX_PART_NUMBER];
                                string manufacturer = SA[LINE_PIDX_MANUFACTURER];
                                string documPost = SA[LINE_PIDX_TU];
                                if (subst_map.TryGetValue(partNumber, out var ptm)) {
                                    partNumber = ptm.pn;
                                    manufacturer = ptm.manuf;
                                    documPost = ptm.tu;
                                }
                                var componentPropList = new List<CompProperties>() {
                                    new CompProperties(CompProperties.Name.name, partNumber),
                                    new CompProperties(CompProperties.Name.designator, SA[LINE_PIDX_REFDES]),
                                    new CompProperties(CompProperties.Name.docum, documPost),
                                    new CompProperties(CompProperties.Name.manuf, manufacturer),
                                    new CompProperties(CompProperties.Name.aux, SA[LINE_PIDX_AUX]),
                                    //new CompProperties(valueName, SA[LINE_PIDX_VALUE])
                                };
                                for (int i = 0; i < nTimes; i++) 
                                    rslt_list.Add(componentPropList);
                            }
                            else {
                                var section = Spec_StrToSection.GetValueRE(SA[LINE_PIDX_PART_NUMBER]);
                                if (section != SpSections.Documentation) { 
                                    var dqty = DoubleComma.Parse( SA[LINE_PIDX_QTY] );
                                    if (dqty <= 0.0) 
                                        throw new Exception("Wrong Quantity");
                                }

                                var componentPropList = new List<CompProperties>() {
                                    new CompProperties(CompProperties.Name.section, SA[LINE_PIDX_PART_NUMBER]),
                                    new CompProperties(CompProperties.Name.shifr, SA[LINE_PIDX_VALUE]),
                                    new CompProperties(CompProperties.Name.qty, SA[LINE_PIDX_QTY]),
                                    new CompProperties(CompProperties.Name.manuf, SA[LINE_PIDX_MANUFACTURER]),
                                    new CompProperties(CompProperties.Name.name, SA[LINE_PIDX_PN]),
                                    new CompProperties(CompProperties.Name.docum, SA[LINE_PIDX_TU]),
                                    new CompProperties(CompProperties.Name.aux, SA[LINE_PIDX_AUX]),
                                    new CompProperties(CompProperties.Name.format, SA[LINE_PIDX_REFDES]),
                                    new CompProperties(CompProperties.Name.pos, SA[LINE_PIDX_N])
                                };
                                for (int i = 0; i < nTimes; i++)
                                    oth_list.Add(componentPropList);
                            }
                        }
                    } catch (Exception ex) {
                        throw new Exception(dataFile.MakeExceptionString(ex.Message));
                    }
                } // using
                if (bCompsNOthers) { 
                    var fn_oth = Path.ChangeExtension(fn, null) + "_add.txt"; // список дополнительных деталей и материалов
                    if (File.Exists(fn_oth)) {
                        var r = LoadOneFile(fn_oth, nTimes, bCompsNOthers: false);
                        oth_list.AddRange(r.others);
                    }
                    var repline = $"{nTimes} x {Path.GetFileName(fn)} = {nTimes} x {rslt_list.Count / nTimes} = {rslt_list.Count} components";
                    if (oth_list.Count > 0) {
                        repline += $"     (+ {nTimes} x {oth_list.Count / nTimes} = {oth_list.Count} other items)";
                    }
                    report.Add(repline);
                }

                return (hdr_info, rslt_list, oth_list);
            }

            perTempSave = new TempSaves();
            specTempSave = new TempSaves();
            vedomostTempSave = new TempSaves();

            List<DielProperties> pcbMaterialsList = new List<DielProperties>();

            #region Открытие и парсинг файлов Mentor

            List<List<CompProperties>> componentsList = new List<List<CompProperties>>();
            List<List<CompProperties>> othersList = new List<List<CompProperties>>();

            List<List<List<CompProperties>>> componentsVariantList = new List<List<List<CompProperties>>>();
            List<List<CompProperties>> componentsPropVariantList = new List<List<CompProperties>>();
            List<CompProperties> componentVariantPropList = new List<CompProperties>();
            List<String> variantNameList = new List<String>();
            List<String> dnfDesignatorsList = new List<String>();
            List<List<String>> dnfVariantDesignatorsList = new List<List<String>>();
            List<String> fittedDesignatorsList = new List<String>();
            List<List<String>> fittedVariantDesignatorsList = new List<List<String>>();

            List<List<string[]>> prjParamsVariantList = new List<List<string[]>>();

            bool bSpecificationConstPosMode = false;


            variantNameList.Add("No Variations");
            //bool isWaitingVariantDescription = false;

            List<CompProperties> otherPropList = new List<CompProperties>();

            int pcbLayersCount = 2;

            //int currentVariant = 0;
            string[] HeaderInfo;

            report.Add($"DocGOST import report from {DateTime.Now}");
            report.Add("");
            report.Add($"Files:");
            {
                var fn = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "Settings", "_subst.csv" );
                if (File.Exists(fn)) { 
                    using (var dataFile = new CsvReader(fn, ';')) {
                        try {
                            string[] SA = dataFile.ReadLineAsStrings(4, true); // table header
                            CommonProc.RaiseIfTrue(SA[0] != "PartNo", "Первая колонка должна называться 'PartNo'");
                            while (!dataFile.Eof()) {
                                SA = dataFile.ReadLineAsStrings(4, true);
                                if (SA[0] == "" || SA[1] == "") continue;
                                subst_map.Add(SA[0], (SA[1], SA[2], SA[3]));
                            }
                        } catch (Exception ex) {
                            throw new Exception(dataFile.MakeExceptionString(ex.Message));
                        }
                    }
                }
            }

            var lrslt = LoadOneFile(pcbPrjFile, 1, bCompsNOthers: true);
            HeaderInfo = lrslt.hdr;
            componentsList = lrslt.comps;
            othersList = lrslt.others;

            report.Add("");
            report.Add($"Total components count = {componentsList.Count}");
            report.Add($"Total other items count = {othersList.Count}");

            #endregion
            report.Add("");
            report.Add("Errors:");

            string ExtractShift(string str) {
                var idx = str.IndexOf(' ');
                if (idx >= 0) return str.Substring(0, idx);
                else return str;
            }

            string PrjShifr = ExtractShift( HeaderInfo[HDR_IDX_SHIFR] );
            string ParentDocShifr = HeaderInfo[HDR_PARENT_SHIFR];
            if (ParentDocShifr == "")
                ParentDocShifr = PrjShifr;
            else
                ParentDocShifr = ExtractShift(ParentDocShifr);
            string PcbShifr = "<PcbShifr>";

            #region Заполнение списков для базы данных проекта
            createPdfMenuItem.IsEnabled = true;
            btnCreatePdf.IsEnabled = true;

            List<PerechenItem> perechenList = new List<PerechenItem>();
            List<SpecificationItem> specList = new List<SpecificationItem>();
            List<VedomostItem> vedomostList = new List<VedomostItem>();
            List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

            int numberOfValidStrings = 0;

            /*tempSpecItem = new SpecificationItem();
            numberOfValidStrings++;
            tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
            tempSpecItem.format = "*)";
            tempSpecItem.oboznachenie = importPcbPrjWindow.prjNumberTextBox.Text + " Д33";
            tempSpecItem.name = "Данные проектирования модуля";
            tempSpecItem.quantity = String.Empty;
            tempSpecItem.note = "*) CD-диск";
            tempSpecItem.spSection = (int)Global.SpSections.Documentation;
            specList.Add(tempSpecItem);*/

            project?.Dispose();
            project = new Data.ProjectDB(projectPath);

            PcbSpecificationItem tempPcbSpecItem = new PcbSpecificationItem();
            tempPcbSpecItem.id = id.makeID(1, specTempSave.GetCurrent());
            tempPcbSpecItem.format = "А1";
            tempPcbSpecItem.oboznachenie = PcbShifr + " СБ";
            tempPcbSpecItem.name = "Сборочный чертёж";
            tempPcbSpecItem.quantity = String.Empty;
            tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
            pcbSpecList.Add(tempPcbSpecItem);

            tempPcbSpecItem = new PcbSpecificationItem();
            tempPcbSpecItem.id = id.makeID(2, specTempSave.GetCurrent());
            tempPcbSpecItem.format = "*)";
            tempPcbSpecItem.oboznachenie = PcbShifr + " Т5М";
            tempPcbSpecItem.name = "Данные проектирования";
            tempPcbSpecItem.quantity = String.Empty;
            tempPcbSpecItem.note = "*) CD-диск";
            tempPcbSpecItem.spSection = (int)Global.SpSections.Documentation;
            pcbSpecList.Add(tempPcbSpecItem);

            int index = 3;
            int coreQuantity = 0; //кол-во ядер в стеке платы
            foreach (DielProperties pcbMaterialItem in pcbMaterialsList) {
                tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                tempPcbSpecItem.format = String.Empty;
                tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                tempPcbSpecItem.oboznachenie = String.Empty;
                string name = String.Empty;
                if (pcbMaterialItem.DielType == 1) {
                    coreQuantity += pcbMaterialItem.Quantity;
                    name = "Стеклотекстолит ";
                } else name = "Препрег ";
                name += pcbMaterialItem.Name + " ";
                name += pcbMaterialItem.Height + " мм";
                tempPcbSpecItem.name = name;
                tempPcbSpecItem.quantity = pcbMaterialItem.Quantity.ToString();
                tempPcbSpecItem.note = String.Empty;
                tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                pcbSpecList.Add(tempPcbSpecItem);
                index++;
            }

            int additionalCopperLayersCount = pcbLayersCount - coreQuantity * 2;
            if (additionalCopperLayersCount > 0) {
                tempPcbSpecItem = new PcbSpecificationItem();
                tempPcbSpecItem.id = id.makeID(index, specTempSave.GetCurrent());
                tempPcbSpecItem.format = String.Empty;
                tempPcbSpecItem.position = SpecificationItem.POS_AUTO;
                tempPcbSpecItem.oboznachenie = String.Empty;
                tempPcbSpecItem.name = "Фольга медная толщиной 18 мкм";
                tempPcbSpecItem.quantity = additionalCopperLayersCount.ToString();
                tempPcbSpecItem.note = String.Empty;
                tempPcbSpecItem.spSection = (int)Global.SpSections.Materials;
                pcbSpecList.Add(tempPcbSpecItem);
            }

            OsnNadpisItem osnNadpisItem = new OsnNadpisItem();

            DisplayPcbSpecValues(pcbSpecList);
            //Сохраняем спецификацию для ПП в БД, потому что она не требует сортировки
            project.BeginTransaction();
            try { 
                for (int i = 0; i < pcbSpecList.Count; i++) {
                    PcbSpecificationItem sd = pcbSpecList[i];
                    sd.id = id.makeID(i + 1, pcbSpecTempSave.GetCurrent());
                    project.AddPcbSpecItem(sd);
                }

                osnNadpisItem.grapha = "2";
                osnNadpisItem.specificationValue = PrjShifr;
                osnNadpisItem.perechenValue = osnNadpisItem.specificationValue + " ПЭ3";
                osnNadpisItem.vedomostValue = osnNadpisItem.specificationValue + " ВП";
                osnNadpisItem.pcbSpecificationValue = PcbShifr;
                project.SaveOsnNadpisItem(osnNadpisItem);

                osnNadpisItem.grapha = "25";
                osnNadpisItem.specificationValue = String.Empty;
                osnNadpisItem.perechenValue = ParentDocShifr; //Для перечня здесь указываем спецификацию
                osnNadpisItem.vedomostValue = ParentDocShifr; //Для ведомости здесь указываем спецификацию
                osnNadpisItem.pcbSpecificationValue = ParentDocShifr; //Для спецификации ПП здесь указываем спецификацию
                project.SaveOsnNadpisItem(osnNadpisItem);

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "1а",
                    specificationValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    perechenValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    vedomostValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_SCH_NAME]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "1a",
                    specificationValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    perechenValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    vedomostValue = HeaderInfo[HDR_IDX_SCH_NAME],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_SCH_NAME]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "9",
                    specificationValue = OrgName,
                    perechenValue = OrgName,
                    vedomostValue = OrgName,
                    pcbSpecificationValue = OrgName
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "11a",
                    specificationValue = HeaderInfo[HDR_IDX_ISPOLNITEL],
                    perechenValue = HeaderInfo[HDR_IDX_ISPOLNITEL],
                    vedomostValue = HeaderInfo[HDR_IDX_ISPOLNITEL],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_ISPOLNITEL]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "11b",
                    specificationValue = HeaderInfo[HDR_IDX_PROVERIL],
                    perechenValue = HeaderInfo[HDR_IDX_PROVERIL],
                    vedomostValue = HeaderInfo[HDR_IDX_PROVERIL],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_PROVERIL]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "11e",
                    specificationValue = HeaderInfo[HDR_IDX_UTVERDIL],
                    perechenValue = HeaderInfo[HDR_IDX_UTVERDIL],
                    vedomostValue = HeaderInfo[HDR_IDX_UTVERDIL],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_UTVERDIL]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "11d",
                    specificationValue = HeaderInfo[HDR_IDX_NORMOKONTROL],
                    perechenValue = HeaderInfo[HDR_IDX_NORMOKONTROL],
                    vedomostValue = HeaderInfo[HDR_IDX_NORMOKONTROL],
                    pcbSpecificationValue = HeaderInfo[HDR_IDX_NORMOKONTROL]
                });

                project.SaveOsnNadpisItem(new OsnNadpisItem() {
                    grapha = "13",
                    specificationValue = SignDate,
                    perechenValue = SignDate,
                    vedomostValue = SignDate,
                    pcbSpecificationValue = SignDate
                });

            } finally {
                project.Commit();
            }


            #region Сохранение выбранных наименований свойств комопонентов в SettingsDB
            var propNameItem = new SettingsItem();
            using (var settingsDB = new SettingsDB()) { 
                settingsDB.BeginTransaction();
                try { 
                    propNameItem.name = "designatorPropName";
                    propNameItem.valueString = "Ref Designator"; // designatorName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "namePropName";
                    propNameItem.valueString = "Part Number"; // nameName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "documPropName";
                    propNameItem.valueString = "Docum"; // documName;
                    settingsDB.SaveSettingItem(propNameItem);

                    propNameItem.name = "notePropName";
                    propNameItem.valueString = "Note"; // manufName;
                    settingsDB.SaveSettingItem(propNameItem);
                } finally {
                    settingsDB.Commit();
                }
            }
            #endregion

            var designators = new Dictionary<string, DesignatorDescriptionItem>();
            using (var designDB = new DesignatorDB()) { 
                var items = designDB.GetAllItems();
                foreach (var item in items) { 
                    designators[item.Designator] = item;
                    item.Group = item.Group.Substring(0, 1).ToUpper() + item.Group.Substring(1, item.Group.Length - 1).ToLower();
                    item.GroupPlural = item.GroupPlural.Substring(0, 1).ToUpper() + item.GroupPlural.Substring(1, item.GroupPlural.Length - 1).ToLower();
                }
            }

            for (int i = 0; i < componentsList.Count; i++) {
                PerechenItem tempPerechen = new PerechenItem();
                SpecificationItem tempSpecification = new SpecificationItem();
                VedomostItem tempVedomost = new VedomostItem();
                PcbSpecificationItem tempPcbSpecification = new PcbSpecificationItem();

                numberOfValidStrings++;
                tempPerechen.id = id.makeID(numberOfValidStrings, perTempSave.GetCurrent());
                tempPerechen.designator = string.Empty;
                tempPerechen.name = string.Empty;
                tempPerechen.quantity = "1";

                tempPerechen.docum = string.Empty;
                tempPerechen.type = string.Empty;
                tempPerechen.group = string.Empty;
                tempPerechen.groupPlural = string.Empty;
                tempPerechen.isNameUnderlined = false;
                tempPerechen.note = string.Empty;
                bool bNoVPI = false;

                for (int j = 0; j < (componentsList[i]).Count; j++) {
                    CompProperties prop;
                    try {
                        prop = (componentsList[i])[j];

                        switch (prop.name) { 
                            case CompProperties.Name.designator: tempPerechen.designator = prop.text; break;
                            case CompProperties.Name.name: tempPerechen.name = prop.text; break;
                            case CompProperties.Name.docum: tempPerechen.docum = prop.text; break;
                            case CompProperties.Name.manuf: tempPerechen.note = prop.text; break;
                            case CompProperties.Name.aux: tempPerechen.auxNote = ReplaceText(prop.text, FLAG_NO_VPI, "", ref bNoVPI); break;
                            //case valueName: {
                            //    if (prop.Text != "")
                            //    tempSpecification.value
                            //    break;
                            //}
                        }
                    } catch {
                        report.Add(j.ToString() + "/" + ((componentsList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + componentsList.Count);
                        //MessageBox.Show(j.ToString() + "/" + ((componentsList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + numberOfStrings);
                    }
                }

                /*if (isDNF == false)*/ {

                    tempSpecification.id = tempPerechen.id;
                    tempSpecification.spSection = (int)Global.SpSections.Other;
                    tempSpecification.format = String.Empty;
                    tempSpecification.zona = String.Empty;
                    tempSpecification.position = String.Empty;
                    tempSpecification.oboznachenie = String.Empty;
                    tempSpecification.name = tempPerechen.name;
                    tempSpecification.quantity = "1";
                    tempSpecification.note = tempPerechen.designator;
                    tempSpecification.group = String.Empty;
                    tempSpecification.docum = tempPerechen.docum;
                    tempSpecification.designator = tempPerechen.designator;
                    tempSpecification.isNameUnderlined = false;

                    tempVedomost.id = tempPerechen.id;
                    tempVedomost.designator = tempPerechen.designator;
                    tempVedomost.name = tempPerechen.name;
                    tempVedomost.kod = String.Empty;
                    tempVedomost.docum = tempPerechen.docum;
                    tempVedomost.supplier = tempPerechen.note;
                    tempVedomost.belongs = String.Empty;
                    tempVedomost.quantityIzdelie = "1";
                    tempVedomost.quantityComplects = String.Empty;
                    tempVedomost.quantityTotal = "1";
                    tempVedomost.note = String.Empty;
                    tempVedomost.auxNote = tempPerechen.auxNote;
                    tempVedomost.isNameUnderlined = false;

                    string group = string.Empty;
                    string itemdesgroup = Global.ExtractDesignatorGroupName( Global.GetDesignatorValue(tempPerechen.designator) );
                    if (designators.TryGetValue(itemdesgroup, out var desDescr)) { 
                        tempPerechen.group = desDescr.Group;
                        tempPerechen.groupPlural = desDescr.GroupPlural;

                        group = tempPerechen.group;

                        tempSpecification.group = group;
                        tempPcbSpecification.group = group;
                        tempVedomost.group = group;
                        tempVedomost.groupPlural = tempPerechen.groupPlural;
                    }
                    else {
                        report.Add($"Не удалось определить, к какой группе принадлежит элемент '{tempPerechen.designator}'");
                        //var r = MessageBox.Show($"Не удалось определить, к какой группе принадлежит элемент '{tempPerechen.designator}'", "Ошибка", MessageBoxButton.OKCancel, MessageBoxImage.Error);
                        //if (r == MessageBoxResult.Cancel) return;
                    }

                    tempSpecification.name = group + " " + tempSpecification.name + " " + tempSpecification.docum;

                    perechenList.Add(tempPerechen);
                    specList.Add(tempSpecification);
                    if (!bNoVPI)
                        vedomostList.Add(tempVedomost);
                } /*else {
                    numberOfValidStrings--;
                }*/
            } // for

            var materials_map = new Dictionary<(string name, string tu, string edizm), (SpecificationItem si, VedomostItem vi)>();
            bool bHaveDocAdditionals = false;
            for (int i = 0; i < othersList.Count; i++) {
                SpecificationItem tempSpecification = new SpecificationItem();
                VedomostItem tempVedomost = new VedomostItem();

                numberOfValidStrings++;

                tempSpecification.id = id.makeID(numberOfValidStrings, perTempSave.GetCurrent());
                tempSpecification.zona = String.Empty;
                tempSpecification.group = String.Empty;
                tempSpecification.designator = String.Empty;
                tempSpecification.isNameUnderlined = false;
                bool bNoVPI = false;

                var litem = othersList[i];
                for (int j = 0; j < litem.Count; j++) {
                    try {
                        var prop = litem[j];
                        switch (prop.name) {
                            case CompProperties.Name.section: 
                                var sec = Spec_StrToSection[prop.text];
                                tempSpecification.spSection = (int)sec; 
                                tempVedomost.group = tempVedomost.groupPlural = sec.FullName(); 
                                if (tempSpecification.spSection == (int)SpSections.Documentation) bHaveDocAdditionals = true; 
                                break;
                            case CompProperties.Name.shifr: tempSpecification.oboznachenie = prop.text; break;
                            case CompProperties.Name.qty: tempSpecification.quantity = prop.text; break;
                            case CompProperties.Name.manuf: tempVedomost.supplier = prop.text; break;
                            case CompProperties.Name.name: tempSpecification.name = prop.text; break;
                            case CompProperties.Name.docum: tempSpecification.docum = prop.text; break;
                            case CompProperties.Name.aux: tempSpecification.note = ReplaceText(prop.text, FLAG_NO_VPI, "", ref bNoVPI); break;
                            case CompProperties.Name.format: tempSpecification.format = prop.text; break;
                            case CompProperties.Name.pos: tempSpecification.position = prop.text; break;
                        }
                    } catch {
                        report.Add(j.ToString() + "/" + ((othersList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + othersList.Count);
                        //MessageBox.Show(j.ToString() + "/" + ((componentsList[i]).Capacity - 1).ToString() + ' ' + i.ToString() + "/" + numberOfStrings);
                    }
                }
                if (tempSpecification.spSection == (int)SpSections.Documentation) {
                    var bDummy = false;
                    tempSpecification.oboznachenie = ReplaceText(tempSpecification.oboznachenie, FLAG_DEC_N, PrjShifr, ref bDummy);
                }
                if (tempSpecification.position == "")
                    tempSpecification.position = SpecificationItem.POS_AUTO;
                else
                    bSpecificationConstPosMode = true;

                tempVedomost.id = tempSpecification.id;
                tempVedomost.designator = tempSpecification.designator;
                tempVedomost.name = tempSpecification.name;
                tempVedomost.kod = String.Empty;
                tempVedomost.docum = tempSpecification.docum;
                //tempVedomost.supplier = String.Empty;
                tempVedomost.belongs = String.Empty;
                tempVedomost.quantityIzdelie = tempSpecification.quantity;
                tempVedomost.quantityComplects = String.Empty;
                tempVedomost.quantityTotal = tempSpecification.quantity;
                tempVedomost.note = tempSpecification.note;
                tempVedomost.auxNote = String.Empty;
                tempVedomost.isNameUnderlined = false;

                if (tempSpecification.oboznachenie == "" && tempSpecification.docum != "")
                    tempSpecification.name += " " + tempSpecification.docum;

                // сливаем повторяющиеся материалы/изделия
                if (!bNoVPI)
                    if ( materials_map.TryGetValue((tempVedomost.name, tempVedomost.docum, tempVedomost.note), out var item) ) {
                        if ( DoubleComma.TryParse(item.si.quantity, out var item_qty) 
                            && DoubleComma.TryParse(tempSpecification.quantity, out var cur_qty) ) {
                            item_qty += cur_qty;
                            item.si.quantity = item_qty.ToStringComma();
                            item.vi.quantityIzdelie = item.vi.quantityTotal = item.si.quantity;
                            numberOfValidStrings--;
                            continue;
                        }
                    }
                    else 
                        materials_map.Add( (tempVedomost.name, tempVedomost.docum, tempVedomost.note), (tempSpecification, tempVedomost) );

                specList.Add(tempSpecification);
                if (tempSpecification.spSection != (int)SpSections.Documentation) { 
                    if (!bNoVPI)
                        vedomostList.Add(tempVedomost);
                }
                else
                    tempSpecification.position = "";
            } // for

            if (!bHaveDocAdditionals) { // если есть хотя бы один документ в дополнительных, то стандартные документы не добавляем
                SpecificationItem tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = PrjShifr + " СБ";
                tempSpecItem.name = "Сборочный чертёж";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = PrjShifr + " Э3";
                tempSpecItem.name = "Схема электрическая принципиальная";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А4";
                tempSpecItem.oboznachenie = PrjShifr + " ПЭ3";
                tempSpecItem.name = "Перечень элементов";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);

                tempSpecItem = new SpecificationItem();
                numberOfValidStrings++;
                tempSpecItem.id = id.makeID(numberOfValidStrings, specTempSave.GetCurrent());
                tempSpecItem.format = "А3";
                tempSpecItem.oboznachenie = PrjShifr + " ВП";
                tempSpecItem.name = "Ведомость покупных изделий";
                tempSpecItem.quantity = String.Empty;
                tempSpecItem.spSection = (int)Global.SpSections.Documentation;
                specList.Add(tempSpecItem);
            }

            var hiePerechenBlockList = new List<HiePerechenBlock>();
            {
                int CompareItems(PerechenItem a, PerechenItem b) {
                    int rslt = a.name.CompareTo(b.name);
                    if (rslt != 0) return rslt;
                    rslt = a.note.CompareTo(b.note);
                    if (rslt != 0) return rslt;
                    long desa = Global.ExtractDesignatorGroupAndSelfNum( Global.GetDesignatorValue(a.designator) );
                    long desb = Global.ExtractDesignatorGroupAndSelfNum( Global.GetDesignatorValue(b.designator) );
                    return Math.Sign( desa - desb );
                }

                int CompareWithoutBlN(KeyValuePair<int, List<PerechenItem>> a, KeyValuePair<int, List<PerechenItem>> b) {
                    int rslt = a.Value.Count - b.Value.Count;
                    if (rslt != 0) return rslt;
                    for (int i = 0; i < a.Value.Count; i++) {
                        rslt = CompareItems(a.Value[i], b.Value[i]);
                        if (rslt != 0) return rslt;
                    }
                    return 0;
                }

                int CompareWithBlN(KeyValuePair<int, List<PerechenItem>> a, KeyValuePair<int, List<PerechenItem>> b) {
                    var rslt = CompareWithoutBlN(a, b);
                    if (rslt == 0)
                        rslt = a.Key - b.Key;
                    return rslt;
                }

                hiePerechenBlockList.Add(new HiePerechenBlock() {
                    perechenItems = new List<PerechenItem>()
                });
                var blmap = new Dictionary<int, List<PerechenItem>>();
                foreach (var pi in perechenList) {
                    int bln = Global.ExtractDesignatorHieBlockNum( MakeDesignatorForOrdering_v2(pi.designator) );
                    if (bln == 0) {
                        hiePerechenBlockList[0].perechenItems.Add(pi);
                        continue;
                    }
                    List<PerechenItem> list;
                    if (!blmap.TryGetValue(bln, out list)) {
                        list = new List<PerechenItem>();
                        blmap.Add(bln, list);
                    }
                    list.Add( pi );
                }
                var blist = blmap.ToList();
                blist.Sort(CompareWithBlN);

                for (int i = 0; i < blist.Count; i++) {
                    if (i == 0 || CompareWithoutBlN(blist[i], blist[i - 1]) != 0)
                        hiePerechenBlockList.Add( new HiePerechenBlock() {
                            sameBlockNums = new List<int> { blist[i].Key },
                            perechenItems = blist[i].Value
                        } );
                    else 
                        hiePerechenBlockList.Last().sameBlockNums.Add(blist[i].Key);
                }

                for (int i = 0; i < hiePerechenBlockList.Count; i++) {
                    foreach (var pi in hiePerechenBlockList[i].perechenItems) {
                        long ides = Global.GetDesignatorValue(pi.designator);
                        pi.designator = Global.ExtractDesignatorGroupName(ides) + Global.ExtractDesignatorSelfNum(ides);
                    }
                    hiePerechenBlockList[i].perechenItems.Sort((a, b) => Math.Sign(Global.GetDesignatorValue(a.designator) - Global.GetDesignatorValue(b.designator)));
                }

                hiePerechenBlockList.Sort( (a, b) => (a.sameBlockNums == null ? -1 : a.sameBlockNums.Min()) - (b.sameBlockNums == null ? -1 : b.sameBlockNums.Min()) );
                //Debug.WriteLine(blist[i].Key + " " + CompareWithoutBlN(blist[i], blist[i-1]) ); 
                //foreach (var item in hiePerechenBlockList) {
                //    Debug.WriteLine((item.sameBlockNums == null ? "" : string.Join(",", item.sameBlockNums)) + " --- " + item.perechenItems.Count); 
                //}
            }

            //Сортировка по поз. обозначению
            List<PerechenItem> perechenListSorted = new List<PerechenItem>();
            List<SpecificationItem> specOtherListSorted = new List<SpecificationItem>();
            List<VedomostItem> vedomostListSorted = new List<VedomostItem>();

            perechenListSorted = perechenList.OrderBy(x => MakeDesignatorForOrdering_v2(x.designator, report)).ToList();
            specOtherListSorted = specList.Where(x => x.spSection == ((int)Global.SpSections.Other)).OrderBy(x => SpecificationOperations.MakeDesignator_Special(x.designator)).ToList();
            vedomostListSorted = vedomostList.OrderBy(x => MakeDesignatorForOrdering_v2(x.designator)).ToList();

            for (int i = 0; i < perechenListSorted.Count; i++) 
                perechenListSorted[i].id = id.makeID(i + 1, perTempSave.GetCurrent());
            for (int i = 0; i < specOtherListSorted.Count; i++) {
                specOtherListSorted[i].id = id.makeID(i + 1 + (numberOfValidStrings - specOtherListSorted.Count), specTempSave.GetCurrent());
            }
            for (int i = 0; i < vedomostListSorted.Count; i++) 
                vedomostListSorted[i].id = id.makeID(i + 1, vedomostTempSave.GetCurrent());

            saveProjectMenuItem.IsEnabled = true;
            undoMenuItem.IsEnabled = true;
            redoMenuItem.IsEnabled = true;
            #endregion
            groupByNameSaveAndPrint_v2(hiePerechenBlockList, specList.Where(x => x.spSection != ((int)Global.SpSections.Other)).ToList(), specOtherListSorted, 
                vedomostListSorted, bSpecificationConstPosMode); 

            //waitGrid.Visibility = Visibility.Hidden;
            var repfn = Path.ChangeExtension(projectPath, null) + "_report.log";
            using (var fs = new StreamWriter(repfn)) {
                foreach (var line in report) 
                    fs.WriteLine(line);
            }
            Process.Start(repfn);
        }

        //private int MakeDesignatorForOrdering(string designator) {
        //    int result = 0;
        //    if (designator.Length > 1) {
        //        if (Char.IsDigit(designator[1])) {
        //            result = ((designator[0]) << 24) + int.Parse(designator.Substring(1, designator.Length - 1));
        //        } else if (designator[1] == '?') {
        //            result = ((designator[0]) << 24) + 0;
        //            MessageBox.Show("В схеме есть не пронумерованный компонент " + designator);
        //        } else if (Char.IsDigit(designator[2])) {
        //            if (designator.Length >= 3)
        //                result = ((designator[0]) << 24) + (designator[1] << 16) + int.Parse(designator.Substring(2, designator.Length - 2));
        //        } else if (designator[2] == '?') {
        //            if (designator.Length >= 3)
        //                result = ((designator[0]) << 24) + (designator[1] << 16) + 0;
        //            MessageBox.Show("В схеме есть не пронумерованный компонент " + designator);
        //        } else if (Char.IsDigit(designator[3])) //Для комонентов с обозначением из 3 букв, например, "PCB1"
        //          {
        //            if (designator.Length >= 4)
        //                result = ((designator[0]) << 24) + (designator[1] << 16) + int.Parse(designator.Substring(3, designator.Length - 3));
        //        }
        //    }
        //    return result;
        //}

        /// <summary> Формирование числа типа int для правильной сортировки позиционных обозначений,
        /// так как в случае простой сортировки по алфавиту результат неправильный, например,
        /// сортируется C1, C15, C2 вместо C1, C2, C15.<</summary>
        private long MakeDesignatorForOrdering(string designator) {
            return Global.GetDesignatorValue(designator, (str) => { ShowError(str); });
        }

        private long MakeDesignatorForOrdering_v2(string designator, List<string> report = null) {
            return Global.GetDesignatorValue(designator, (str) => { if (report != null) report.Add(str); });
        }

        /// <summary> Чтение строк вида ='Str1'+'Str2' при чтении данных из проекта Altium Designer (.PrjPcb) </summary>
        private string MakeComplexStringIfItIs(string text, List<ComponentProperties> componentsList)
        {
            bool isComplexName = false;
            string result = text;

            if (text[0] == '=')
            {
                isComplexName = true;
                string tempStr = String.Empty;
                string origStr = text.Substring(1); //Удаляем пробелы и знак равно в начале строки
                string[] otherPropNames = origStr.Split(new char[] { '+' });
                for (int j = 0; j < otherPropNames.Length; j++)
                {
                    if ((otherPropNames[j].First() == '\'') && (otherPropNames[j].Last() == '\''))
                        tempStr += otherPropNames[j].Substring(1, otherPropNames[j].Length - 2);
                    else
                    {
                        try
                        {
                            tempStr += componentsList.Where(x => x.Name == otherPropNames[j]).First().Text;
                        }
                        catch
                        {
                            isComplexName = false;
                        }
                    }
                }
                if (isComplexName) result = tempStr;
            }
            return result;
        }

        /// <summary> Отображение данных спецификации и перечня пользователю из текущего временного сохранения проекта </summary>
        private void DisplayAllValues()
        {
            //Вывод данных перечня в окно программы:
            int length = project.GetPerechenLength(perTempSave.GetCurrent());
            List<PerechenItem> resultPer = new List<PerechenItem>(length);

            for (int i = 1; i <= length; i++)
            {
                resultPer.Add(project.GetPerechenItem(i, perTempSave.GetCurrent()));
            }

            DisplayPerValues(resultPer);

            //Вывод данных спецификации в окно программы:
            length = project.GetSpecLength(specTempSave.GetCurrent());
            List<SpecificationItem> resultSpec = new List<SpecificationItem>(length);

            for (int i = 1; i <= length; i++)
            {
                resultSpec.Add(project.GetSpecItem(i, specTempSave.GetCurrent()));
            }

            DisplaySpecValues(resultSpec);

            //Вывод данных ведомости в окно программы:
            length = project.GetVedomostLength(vedomostTempSave.GetCurrent());
            List<VedomostItem> resultVedomost = new List<VedomostItem>(length);

            for (int i = 1; i <= length; i++)
            {
                resultVedomost.Add(project.GetVedomostItem(i, vedomostTempSave.GetCurrent()));
            }

            DisplayVedomostValues(resultVedomost);

            //Вывод данных спецификации платы в окно программы:
            length = project.GetPcbSpecLength(pcbSpecTempSave.GetCurrent());
            List<PcbSpecificationItem> resultPcbSpec = new List<PcbSpecificationItem>(length);

            for (int i = 1; i <= length; i++)
            {
                resultPcbSpec.Add(project.GetPcbSpecItem(i, pcbSpecTempSave.GetCurrent()));
            }

            DisplayPcbSpecValues(resultPcbSpec);
        }

        /// <summary>
        /// Отображение данных спецификации для пользователя
        /// </summary>
        /// <param name="pData"> Список с данными спецификации для отображения</param>
        private void DisplaySpecValues(List<SpecificationItem> sData)
        {
            //Вывод данных в окно программы:
            if (sData == null)
            {
                documSpecListView.ItemsSource = null;
                compleksiSpecListView.ItemsSource = null;
                sborEdSpecListView.ItemsSource = null;
                detailsSpecListView.ItemsSource = null;
                standartSpecListView.ItemsSource = null;
                otherSpecListView.ItemsSource = null;
                materialsSpecListView.ItemsSource = null;
                complectsSpecListView.ItemsSource = null;
            }
            else
            {

                //Заполнение раздела "Документация"

                List<SpecificationItem> documRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Documentation)).ToList();
                documSpecListView.ItemsSource = documRazdelList;

                //Заполнение раздела "Комплексы"
                List<SpecificationItem> compleksiRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Compleksi)).ToList();
                compleksiSpecListView.ItemsSource = compleksiRazdelList;

                //Заполнение раздела "Сборочные единицы"
                List<SpecificationItem> sborEdRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.SborEd)).ToList();
                sborEdSpecListView.ItemsSource = sborEdRazdelList;

                //Заполнение раздела "Детали"
                List<SpecificationItem> detailsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Details)).ToList();
                detailsSpecListView.ItemsSource = detailsRazdelList;

                //Заполнение раздела "Стандартные изделия"
                List<SpecificationItem> standartRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Standard)).ToList();
                standartSpecListView.ItemsSource = standartRazdelList;

                //Заполнение раздела "Прочие"
                List<SpecificationItem> otherRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Other)).ToList();
                otherRazdelList = sData.Where(x => x.spSection == (int)Global.SpSections.Other).ToList();
                otherSpecListView.ItemsSource = otherRazdelList;

                //Заполнение раздела "Материалы"
                List<SpecificationItem> materialsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Materials)).ToList();
                materialsSpecListView.ItemsSource = materialsRazdelList;

                //Заполнение раздела "Комплекты"
                List<SpecificationItem> complectsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Compleсts)).ToList();
                complectsSpecListView.ItemsSource = complectsRazdelList;
            }

        }

        /// <summary>
        /// Отображение данных спецификации для пользователя
        /// </summary>
        /// <param name="pData"> Список с данными спецификации для отображения</param>
        private void DisplayPcbSpecValues(List<PcbSpecificationItem> sData)
        {
            //Вывод данных в окно программы:
            if (sData == null)
            {
                documPcbSpecListView.ItemsSource = null;
                compleksiPcbSpecListView.ItemsSource = null;
                sborEdPcbSpecListView.ItemsSource = null;
                detailsPcbSpecListView.ItemsSource = null;
                standartPcbSpecListView.ItemsSource = null;
                otherPcbSpecListView.ItemsSource = null;
                materialsPcbSpecListView.ItemsSource = null;
                complectsPcbSpecListView.ItemsSource = null;
            }
            else
            {

                //Заполнение раздела "Документация"

                List<PcbSpecificationItem> documRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Documentation)).ToList();
                documPcbSpecListView.ItemsSource = documRazdelList;

                //Заполнение раздела "Комплексы"
                List<PcbSpecificationItem> compleksiRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Compleksi)).ToList();
                compleksiPcbSpecListView.ItemsSource = compleksiRazdelList;

                //Заполнение раздела "Сборочные единицы"
                List<PcbSpecificationItem> sborEdRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.SborEd)).ToList();
                sborEdPcbSpecListView.ItemsSource = sborEdRazdelList;

                //Заполнение раздела "Детали"
                List<PcbSpecificationItem> detailsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Details)).ToList();
                detailsPcbSpecListView.ItemsSource = detailsRazdelList;

                //Заполнение раздела "Стандартные изделия"
                List<PcbSpecificationItem> standartRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Standard)).ToList();
                standartPcbSpecListView.ItemsSource = standartRazdelList;

                //Заполнение раздела "Прочие"
                List<PcbSpecificationItem> otherRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Other)).ToList();
                otherRazdelList = sData.Where(x => x.spSection == (int)Global.SpSections.Other).ToList();
                otherPcbSpecListView.ItemsSource = otherRazdelList;

                //Заполнение раздела "Материалы"
                List<PcbSpecificationItem> materialsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Materials)).ToList();
                materialsPcbSpecListView.ItemsSource = materialsRazdelList;

                //Заполнение раздела "Комплекты"
                List<PcbSpecificationItem> complectsRazdelList = (sData.Where(x => x.spSection == (int)Global.SpSections.Compleсts)).ToList();
                complectsPcbSpecListView.ItemsSource = complectsRazdelList;
            }

        }

        /// <summary>
        /// Отображение данных перечня для пользователя
        /// </summary>
        /// <param name="pData"> Список с данными перечня элементов для отображения</param>
        private void DisplayPerValues(List<PerechenItem> pData)
        {
            int pageNumber = 1;
            if (pData != null)
                for (int i = 0; i < pData.Count; i++)
                {
                    if (i < 23) pageNumber = 1;
                    else pageNumber = (i - 23) / 29 + 2;

                    pData[i].page = pageNumber.ToString();
                }

            //Вывод данных в окно программы: 
            perechenListView.ItemsSource = pData;

        }

        /// <summary>
        /// Отображение данных ведомости для пользователя
        /// </summary>
        /// <param name="vData"> Список с данными ведомости для отображения</param>
        private void DisplayVedomostValues(List<VedomostItem> vData)
        {
            //Вывод данных в окно программы: 
            vedomostListView.ItemsSource = vData;
        }

        /// <summary> Создание PDF-файлов перечня и спецификации </summary>
        private void CreatePdf_Click(object sender, RoutedEventArgs e)
        {
            try {
                Custom_24_25 custom_2425 = null;
                var fn = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings", "_custom2425.csv");
                if (File.Exists(fn)) {
                    custom_2425 = new Custom_24_25();
                    using (var dataFile = new CsvReader(fn, '\t')) {
                        try {
                            string[] SA = dataFile.ReadLineAsStrings(-1, true); // table header
                            int LINE_CNT = SA.Length;
                            if (LINE_CNT <= 2 || (LINE_CNT - 2) % 3 != 0)
                                throw new Exception("Неверное число колонок в заголовке");
                            while (!dataFile.Eof()) {
                                SA = dataFile.ReadLineAsStrings(LINE_CNT, true);
                                var ypos = SA[0].ToFloat32(0, 999);
                                var height = SA[1].ToFloat32(0, 999);
                                var cells = new List <(float widths_mm, string text, bool is_align_center)>();
                                var CELL_CNT = (LINE_CNT - 2) / 3;
                                for (int i = 0; i < CELL_CNT; i++) 
                                    cells.Add( (SA[2 + i*3 + 0].ToFloat32(0, 999), SA[2 + i*3 + 1], SA[2 + i*3 + 2].ToUpper() == "C") );
                                custom_2425.Add((ypos, height, cells));
                            }
                        } catch (Exception ex) {
                            throw new Exception(dataFile.MakeExceptionString(ex.Message));
                        }
                    }
                }

                if (chkExportPerechen.IsChecked == false && chkExportSpec.IsChecked == false && chkExportVedomost.IsChecked == false) { 
                    ShowError("Нужно выбрать хотя бы один вариант вывода");
                    return;
                }

                int startPage = (startFromSecondCheckBox.IsChecked == false) ? 1 : 2;
                bool addListRegistr = (addListRegistrCheckBox.IsChecked == true);

                if (chkExportPerechen.IsChecked == true) { 
                    var pdfPath = Path.ChangeExtension(projectPath, null) + " " + "ПЭ.pdf";
                    using (var pdf = new PdfOperations(projectPath, custom_2425))
                        pdf.CreatePerechen(pdfPath, startPage, addListRegistr, perTempSave.GetCurrent());
                    if (!bSilentMode) System.Diagnostics.Process.Start(pdfPath); //открываем pdf файл
                }

                if (chkExportSpec.IsChecked == true) {
                    var pdfPath = Path.ChangeExtension(projectPath, null) + " " + "Спецификация.pdf";
                    using (var pdf = new PdfOperations(projectPath, custom_2425))
                        pdf.CreateSpecification(pdfPath, startPage, addListRegistr, specTempSave.GetCurrent());
                    if (!bSilentMode) System.Diagnostics.Process.Start(pdfPath); //открываем pdf файл

                    if (isPcbMultilayer == true) // если плата - сборочная единица
                    {
                        pdfPath = Path.ChangeExtension(projectPath, null) + " " + "Спецификация ПП.pdf";
                        using (var pdf = new PdfOperations(projectPath, custom_2425))
                            pdf.CreatePcbSpecification(pdfPath, startPage, addListRegistr, pcbSpecTempSave.GetCurrent());
                        if (!bSilentMode) System.Diagnostics.Process.Start(pdfPath); //открываем pdf файл
                    }
                }

                if (chkExportVedomost.IsChecked == true) {
                    var pdfPath = Path.ChangeExtension(projectPath, null) + " " + "ВП.pdf";
                    using (var pdf = new PdfOperations(projectPath, custom_2425)) 
                        pdf.CreateVedomost(pdfPath, startPage, addListRegistr, vedomostTempSave.GetCurrent());
                    if (!bSilentMode) System.Diagnostics.Process.Start(pdfPath); //открываем pdf файл
                    CreateVedomostCsv(Path.ChangeExtension(projectPath, null) + " " + "ВП.csv", vedomostTempSave.GetCurrent());
                }
            } catch (Exception ex) {
                ShowError(ex.Message);
            }
        }

        void CreateVedomostCsv(string fileName, int tempNumber) { // Ведомость ПИ для внутреннего использования
            int numberOfValidStrings = project.GetVedomostLength(tempNumber);
            List<VedomostItem> vData = new List<VedomostItem>();
            for (int i = 1; i <= numberOfValidStrings; i++) {
                vData.Add(project.GetVedomostItem(i, tempNumber));
            }
            using (var writer = new CsvWriter(fileName)) {
                writer.WriteLine( CsvWriter.MakeStringFromStrings(new string[]{ "Part Number", "Quantity", "Manufacturer", "Note" }) );
                foreach (var item in vData) {
                    writer.WriteLine( CsvWriter.MakeStringFromStrings( new string[] {
                        item.isNameUnderlined ? "#### " + item.name : item.name + item.docum == "" ? "" : " " + item.docum,
                        item.quantityTotal,  
                        item.supplier, 
                        item.auxNote != null ? item.auxNote.Replace("\\n", "  ") : ""
                    } ) );
                }
            }
        }


        /// <summary> При закрытии окна программы выполняется проверка, сохранён ли проект, и показывается соответствующее диалоговое окно </summary>        
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if ((perTempSave != null) && (specTempSave != null) && (vedomostTempSave != null) && (pcbSpecTempSave != null) && !bSilentMode)
            {
                if ((perTempSave.GetCurrent() != perTempSave.GetLastSavedState()) |
                    (specTempSave.GetCurrent() != specTempSave.GetLastSavedState()) |
                    (vedomostTempSave.GetCurrent() != vedomostTempSave.GetLastSavedState()) |
                    (pcbSpecTempSave.GetCurrent() != pcbSpecTempSave.GetLastSavedState()))
                {
                    var closingDialogResult = MessageBox.Show("Проект не сохранён. Сохранить проект перед закрытием?", "Сохранение проекта", MessageBoxButton.YesNoCancel);
                    if (closingDialogResult == MessageBoxResult.Yes) 
                        project.Save(perTempSave.GetCurrent(), specTempSave.GetCurrent(), vedomostTempSave.GetCurrent(), pcbSpecTempSave.GetCurrent());
                    else if (closingDialogResult == MessageBoxResult.Cancel) { 
                        e.Cancel = true;
                        return;
                    }
                }
                project.DeleteTempData();
            }
        }

        /// <summary> Группировка элементов перечня и спецификации, их запись в базу данных проекта и отображение пользователю </summary>
        /// <remarks> Вызывается после импорта новых данных из файла </remarks>
        /// <param name="pData"> Список с данными для перечня элементов, которые будут сгруппированы</param>
        /// <param name="sData"> Список с данными спецификации без данных раздела "Прочие изделия"</param>
        /// <param name="sOtherData"> Список с данными спецификации из раздела данных "Прочие изделия", которые будут сгруппированы</param>
        /// <param name="vData"> Список с данными для ведомости, которые будут сгруппированы</param>
        private void groupByNameSaveAndPrint(List<PerechenItem> pData, List<SpecificationItem> sData, List<SpecificationItem> sOtherData, List<VedomostItem> vData)
        {

            int numOfPerechenValidStrings = pData.Count;
            int numOfSpecificationStrings = sOtherData.Count;
            int numOfVedomostValidStrings = vData.Count;

            if (numOfPerechenValidStrings > 1)
                (new PerechenOperations()).groupPerechenElements(ref pData, ref numOfPerechenValidStrings);

            if (numOfSpecificationStrings > 1) 
                sOtherData = SpecificationOperations.groupSpecificationElements(sOtherData, false, ref numOfSpecificationStrings);

            if (numOfVedomostValidStrings > 1)
                vData = (new VedomostOperations()).groupVedomostElements(vData, ref numOfVedomostValidStrings);

            _SaveAndPrint(pData, sData, sOtherData, vData, numOfPerechenValidStrings, numOfSpecificationStrings, numOfVedomostValidStrings);
        }

        private void groupByNameSaveAndPrint_v2(List<HiePerechenBlock> hiePerechen, List<SpecificationItem> sData, List<SpecificationItem> sOtherData,
                List<VedomostItem> vData, bool bSpecificationConstPosMode) {

            int numOfSpecificationStrings = sOtherData.Count;
            int numOfVedomostValidStrings = vData.Count;

            var wholePerechen = new List<PerechenItem>();
            for (int i = 0; i < hiePerechen.Count; i++) {
                if (i > 0) {
                    // header
                    var sbn = hiePerechen[i].sameBlockNums;
                    var rlist = new List<(int start, int cnt)>();
                    for (int j = 0; j < sbn.Count; j++) {
                        if ( j == 0 || sbn[j] != sbn[j-1]+1 )
                            rlist.Add( (sbn[j], 1) );
                        else
                            rlist[rlist.Count - 1] = (rlist[rlist.Count - 1].start, rlist[rlist.Count - 1].cnt+1);
                    }
                    wholePerechen.Add(new PerechenItem());
                    wholePerechen.Add(new PerechenItem());
                    if (PerechenOperations.IsLineLastOnPage(wholePerechen.Count+1)) wholePerechen.Add(new PerechenItem());
                    for (int j = 0; j < rlist.Count; j++) {
                        var pi = new PerechenItem() {
                            name = (j == 0) ? "Блок " + i : "",
                            isNameUnderlined = (j == 0),
                            quantity = (j == 0) ? sbn.Count.ToString() : ""
                        };
                        if (rlist[j].cnt > 2) 
                            pi.designator = $"A{rlist[j].start}...A{rlist[j].start + rlist[j].cnt - 1}";
                        else if (rlist[j].cnt == 2)
                            pi.designator = $"A{rlist[j].start}, A{rlist[j].start + rlist[j].cnt - 1}";
                        else if (j < rlist.Count - 1 && rlist[j+1].cnt == 1) { 
                            pi.designator = $"A{rlist[j].start}, A{rlist[j+1].start}";
                            j++;
                        }
                        else
                            pi.designator = $"A{rlist[j].start}";
                        wholePerechen.Add(pi);
                    }
                }
                var pData2 = PerechenOperations.groupPerechenElements_v2(hiePerechen[i].perechenItems);
                foreach (var item in pData2) {
                    if (item.isNameUnderlined)
                        if (PerechenOperations.IsLineLastOnPage(wholePerechen.Count + 1)) wholePerechen.Add(new PerechenItem());
                    wholePerechen.Add(item);
                }
            }

            if (numOfSpecificationStrings > 1)
                sOtherData = SpecificationOperations.groupSpecificationElements(sOtherData, bSpecificationConstPosMode, ref numOfSpecificationStrings);
            sData = SpecificationOperations.BreakLongLinesOnly(sData);

            if (numOfVedomostValidStrings > 1)
                vData = (new VedomostOperations()).groupVedomostElements(vData, ref numOfVedomostValidStrings, false);

            _SaveAndPrint(wholePerechen, sData, sOtherData, vData, wholePerechen.Count, numOfSpecificationStrings, numOfVedomostValidStrings);
        }

        //Запись значений в файл и вывод сгруппированных строк в окно программы:
        void _SaveAndPrint(List<PerechenItem> pData, List<SpecificationItem> sData, List<SpecificationItem> sOtherData, List<VedomostItem> vData,
            int numOfPerechenValidStrings, int numOfSpecificationStrings, int numOfVedomostValidStrings) {
            List<PerechenItem> perResult = new List<PerechenItem>(numOfPerechenValidStrings);
            List<SpecificationItem> specResult = new List<SpecificationItem>(numOfSpecificationStrings + sData.Count);
            List<VedomostItem> vedomostResult = new List<VedomostItem>(numOfVedomostValidStrings);

            project.BeginTransaction(); 
            try { 
                for (int i = 0; i < numOfPerechenValidStrings; i++)
                {
                    PerechenItem pd = pData[i];
                    pd.name += " " + pd.docum;
                    pd.id = id.makeID(i + 1, perTempSave.GetCurrent());
                    perResult.Add(pd);
                    project.AddPerechenItem(pd);
                }

                specResult.AddRange(sData);
                for (int i = 0; i < sOtherData.Count; i++) {
                    sOtherData[i].id = i + 1;
                    specResult.Add(sOtherData[i]);
                }
                specResult.Sort((a, b) => { 
                    var rslt = a.spSection - b.spSection; 
                    if (rslt != 0) return rslt;
                    rslt = a.id - b.id;
                    return rslt; 
                } );
                for (int i = 0; i < specResult.Count; i++) {
                    specResult[i].id = id.makeID(i + 1, specTempSave.GetCurrent());
                    project.AddSpecItem(specResult[i]);
                }

                //for (int i = 0; i < numOfSpecificationStrings + sData.Count; i++)
                //{
                //    if (i < sData.Count())
                //    {
                //        SpecificationItem sd = sData[i];
                //        sd.id = id.makeID(i + 1, specTempSave.GetCurrent());
                //        specResult.Add(sd);
                //        project.AddSpecItem(sd);
                //    }
                //    else
                //    {
                //        SpecificationItem sd = sOtherData[i - sData.Count()];
                //        sd.id = id.makeID(i + 1, specTempSave.GetCurrent());
                //        specResult.Add(sd);
                //        project.AddSpecItem(sd);
                //    }
                //}

                for (int i = 0; i < numOfVedomostValidStrings; i++)
                {
                    VedomostItem vd = vData[i];
                    vd.id = id.makeID(i + 1, perTempSave.GetCurrent());
                    vedomostResult.Add(vd);
                    project.AddVedomostItem(vd);
                }
            } finally {
                project.Commit();
            }

            DisplayPerValues(perResult);
            DisplaySpecValues(specResult);
            DisplayVedomostValues(vedomostResult);
        }

        /// <summary> Редактирование основной надписи </summary>
        private void osnNadpisButton_Click(object sender, RoutedEventArgs e)
        {
            using (OsnNadpisWindow osnNadpis = new OsnNadpisWindow(projectPath)) { 
                //Показываем диалоговое окно для заполнения граф основной надписи
                osnNadpis.ShowDialog();

                //osnNadpis.Close(); 
            }
        }

        /// <summary> Редактирование расшифровок позиционных обозначений </summary>
        private void desigDescrButton_Click(object sender, RoutedEventArgs e)
        {
            DesigDescrWindow ddWindow = new DesigDescrWindow();
            ddWindow.ShowDialog();
        }

        /// <summary> Изменение ширины столбцов элементов ListView при изменении размеров окна </summary>
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double coef = (perechenListView.ActualWidth - 270) / 742; //742 - сумма ширин всех столбцов, кроме 3 кнопок постоянной ширины по 90 = 270
            designatorPerechenColumn.Width = 120 * coef;
            namePerechenColumn.Width = 410 * coef;
            quantityPerechenColumn.Width = 70 * coef;
            notePerechenColumn.Width = 120 * coef;


            formatSpecColumn.Width = 30 * coef;
            zonaSpecColumn.Width = 30 * coef;
            positionSpecColumn.Width = 30 * coef;
            oboznachSpecColumn.Width = 280 * coef;
            nameSpecColumn.Width = 280 * coef;
            quantitySpecColumn.Width = 30 * coef;
            noteSpecColumn.Width = 40 * coef;

            coef = (vedomostListView.ActualWidth - 210) / 1230; //1230 - сумма ширин всех столбцов, кроме 3 кнопок постоянной ширины по 90 + 60 +60 = 210
            nameVedomostColumn.Width = 210 * coef;
            kodVedomostColumn.Width = 70 * coef;
            documVedomostColumn.Width = 180 * coef;
            supplierVedomostColumn.Width = 110 * coef;
            belongsVedomostColumn.Width = 110 * coef;
            quantityIzdelieVedomostColumn.Width = 120 * coef;
            quantityComplectsVedomostColumn.Width = 120 * coef;
            quantityRegulVedomostColumn.Width = 120 * coef;
            quantityTotalVedomostColumn.Width = 50 * coef;
            noteVedomostColumn.Width = 140 * coef;
        }

        /// <summary> Правка текущей строки перечня </summary>
        private void PerechenEdit_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;

            // Создаём список элементов перечня, хранящийся в оперативной памяти для ускорения работы программы:
            int perLength = project.GetPerechenLength(perTempSave.GetCurrent());
            List<PerechenItem> tempPerList = new List<PerechenItem>(perLength);

            for (int i = 1; i <= perLength; i++)
            {
                tempPerList.Add(project.GetPerechenItem(i, perTempSave.GetCurrent()));
            }

            int strNum = id.getStrNum((b.CommandParameter as PerechenItem).id);


            using (EditPerechenItemWindow editWindow = new EditPerechenItemWindow(projectPath, strNum, perTempSave.GetCurrent(), perTempSave.GetCurrent() + 1)) { 
                if (editWindow.ShowDialog() == true)
                {
                    project.DeletePerechenTempData(perTempSave.SetNext()); // Увеличиваем номер текущего сохранения и одновременно удаляем все последующие сохранения

                    project.BeginTransaction();
                    try { 
                        for (int i = 1; i <= perLength; i++)
                        {
                            if (i != strNum)
                            {
                                tempPerList[i - 1].id = id.makeID(i, perTempSave.GetCurrent());
                                project.AddPerechenItem(tempPerList[i - 1]);
                            }

                        }

                        tempPerList[strNum - 1] = project.GetPerechenItem(strNum, perTempSave.GetCurrent());
                        project.AddPerechenItem(tempPerList[strNum - 1]);
                    } finally { 
                        project.Commit(); 
                    }

                    DisplayPerValues(tempPerList);
                }
            }

        }

        /// <summary> Добавление к перечню пустой строки сверху </summary>
        private void PerechenAdd_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as PerechenItem).id);
            int length = project.GetPerechenLength(perTempSave.GetCurrent());
            List<PerechenItem> perList = new List<PerechenItem>();

            int prevTempSave = perTempSave.GetCurrent();
            project.DeletePerechenTempData(perTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = perTempSave.SetNext(); // Увеличиваем номер текущего сохранения

            for (int i = 1; i <= length + 1; i++)
            {
                PerechenItem perItem = new PerechenItem();

                if (i < strNum)
                {
                    perItem = project.GetPerechenItem(i, prevTempSave);
                    perItem.id = id.makeID(i, currentTempSave);
                    perList.Add(perItem);
                }
                else if (i == strNum)
                {
                    perItem.id = id.makeID(i, currentTempSave);
                    perList.Add(perItem);
                }
                else if (i > strNum)
                {
                    perItem = project.GetPerechenItem(i - 1, prevTempSave);
                    perItem.id = id.makeID(i, currentTempSave);
                    perList.Add(perItem);
                }

            }

            project.BeginTransaction();
            try { 
                for (int i = 0; i < perList.Count; i++) project.AddPerechenItem(perList[i]);
            } finally {
                project.Commit();
            }

            DisplayPerValues(perList);
        }

        /// <summary> Удаление из перечня текущей строки </summary>
        private void PerechenDelete_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as PerechenItem).id);
            int length = project.GetPerechenLength(perTempSave.GetCurrent());
            List<PerechenItem> perList = new List<PerechenItem>();

            int prevTempSave = perTempSave.GetCurrent();
            project.DeletePerechenTempData(perTempSave.GetCurrent()); // Удаляем все последующие сохранения
            perTempSave.SetNext(); // Увеличиваем номер текущего сохранения
            int currentTempSave = perTempSave.GetCurrent();

            for (int i = 1; i <= length; i++)
            {
                PerechenItem perItem = new PerechenItem();

                if (i < strNum)
                {
                    perItem = project.GetPerechenItem(i, prevTempSave);
                    perItem.id = id.makeID(i, currentTempSave);
                    perList.Add(perItem);
                }
                else if (i > strNum)
                {
                    perItem = project.GetPerechenItem(i, prevTempSave);
                    perItem.id = id.makeID(i - 1, currentTempSave);
                    perList.Add(perItem);
                }

            }

            project.BeginTransaction();
            try {
                for (int i = 0; i < perList.Count; i++) project.AddPerechenItem(perList[i]);
            } finally {
                project.Commit();
            }
            DisplayPerValues(perList);
        }

        /// <summary> Правка текущей строки спецификации </summary>
        private void SpecEdit_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;

            // Создаём список элементов перечня, хранящийся в оперативной памяти для ускорения работы программы:
            int length = project.GetSpecLength(specTempSave.GetCurrent());
            List<SpecificationItem> tempList = new List<SpecificationItem>(length);

            for (int i = 1; i <= length; i++)
            {
                tempList.Add(project.GetSpecItem(i, specTempSave.GetCurrent()));
            }

            int strNum = id.getStrNum((b.CommandParameter as SpecificationItem).id);
            int tempNum = specTempSave.GetCurrent();

            using (EditSpecItemWindow editWindow = new EditSpecItemWindow(projectPath, strNum, tempNum, specTempSave.GetCurrent() + 1)) { 
                if (editWindow.ShowDialog() == true)
                {
                    project.DeleteSpecTempData(specTempSave.SetNext()); // Увеличиваем номер текущего сохранения и одновременно удаляем все последующие сохранения               

                    project.BeginTransaction();
                    try {
                        for (int i = 1; i <= length; i++)
                        {
                            if (i != strNum)
                            {
                                tempList[i - 1].id = id.makeID(i, specTempSave.GetCurrent());
                                project.AddSpecItem(tempList[i - 1]);
                            }

                        }
                    } finally {
                        project.Commit();
                    }
                    tempList[strNum - 1] = project.GetSpecItem(strNum, specTempSave.GetCurrent());

                    DisplaySpecValues(tempList);
                }
            }
        }

        /// <summary>
        /// Возвращает номер строки элемента, следующего за последним элементом в разделе спецификации
        /// Используется при добавлении строки спецификации снизу
        /// </summary>
        /// <param name="section">Раздел спецификации</param>
        private int GetSpecNextNumInSection(Global.SpSections section)
        {
            int strNum = 1;

            List<SpecificationItem> sData = project.GetSpecificationList(specTempSave.GetCurrent());
            for (int i = (int)Global.SpSections.Documentation; i <= (int)section; i++)
                strNum += (sData.Where(x => x.spSection == i)).Count();

            return strNum;
        }

        /// <summary> Добавление к спецификации пустой строки сверху </summary>
        private void SpecAdd_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum;

            int specSection = 0;

            if (b.Tag as string == "Документация")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Documentation);
                specSection = (int)Global.SpSections.Documentation;
            }
            else
            if (b.Tag as string == "Комплексы")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Compleksi);
                specSection = (int)Global.SpSections.Compleksi;
            }
            else
            if (b.Tag as string == "Сборочные единицы")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.SborEd);
                specSection = (int)Global.SpSections.SborEd;
            }
            else
            if (b.Tag as string == "Детали")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Details);
                specSection = (int)Global.SpSections.Details;
            }
            else
            if (b.Tag as string == "Стандартные изделия")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Standard);
                specSection = (int)Global.SpSections.Standard;
            }
            else
            if (b.Tag as string == "Прочие изделия")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Other);
                specSection = (int)Global.SpSections.Other;
            }
            else
            if (b.Tag as string == "Материалы")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Materials);
                specSection = (int)Global.SpSections.Materials;
            }
            else
            if (b.Tag as string == "Комплекты")
            {
                //Определяем номер добавляемой строки
                strNum = GetSpecNextNumInSection(Global.SpSections.Compleсts);
                specSection = (int)Global.SpSections.Compleсts;
            }
            else
            {
                strNum = id.getStrNum((b.CommandParameter as SpecificationItem).id);
                specSection = (b.CommandParameter as SpecificationItem).spSection;
            }

            int length = project.GetSpecLength(specTempSave.GetCurrent());
            List<SpecificationItem> specList = new List<SpecificationItem>();

            int prevTempSave = specTempSave.GetCurrent();
            project.DeleteSpecTempData(specTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = specTempSave.SetNext(); // Увеличиваем номер текущего сохранения 

            for (int i = 1; i <= length + 1; i++)
            {
                SpecificationItem specItem = new SpecificationItem();

                if (i < strNum)
                {
                    specItem = project.GetSpecItem(i, prevTempSave);
                    specItem.id = id.makeID(i, currentTempSave);
                }
                else if (i == strNum)
                {
                    specItem = new SpecificationItem();
                    specItem.id = id.makeID(i, currentTempSave);
                    specItem.spSection = specSection;
                    specItem.zona = String.Empty;
                    specItem.position = String.Empty;
                    specItem.oboznachenie = String.Empty;
                    specItem.name = String.Empty;
                    specItem.quantity = String.Empty;
                    specItem.note = String.Empty;
                }
                else if (i > strNum)
                {
                    specItem = project.GetSpecItem(i - 1, prevTempSave);
                    specItem.id = id.makeID(i, currentTempSave);
                }
                specList.Add(specItem);
            }

            project.BeginTransaction();
            try {
                for (int i = 0; i < specList.Count; i++) project.AddSpecItem(specList[i]);
            } finally {
                project.Commit();
            }
            DisplaySpecValues(specList);
        }

        /// <summary> Удаление текущей строки спецификации </summary>
        private void SpecDelete_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as SpecificationItem).id);
            int length = project.GetSpecLength(specTempSave.GetCurrent());
            List<SpecificationItem> specList = new List<SpecificationItem>();

            int prevTempSave = specTempSave.GetCurrent();
            project.DeleteSpecTempData(specTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = specTempSave.SetNext(); // Увеличиваем номер текущего сохранения

            for (int i = 1; i <= length; i++)
            {
                SpecificationItem specItem = new SpecificationItem();

                if (i < strNum)
                {
                    specItem = project.GetSpecItem(i, prevTempSave);
                    specItem.id = id.makeID(i, currentTempSave);
                }
                else if (i > strNum)
                {
                    specItem = project.GetSpecItem(i, prevTempSave);
                    specItem.id = id.makeID(i - 1, currentTempSave);
                }
                if (i != strNum) specList.Add(specItem);

            }
            project.BeginTransaction();
            try {
                for (int i = 0; i < specList.Count; i++) project.AddSpecItem(specList[i]);
            } finally {
                project.Commit();
            }
            DisplaySpecValues(specList);
        }

        /// <summary> Правка текущей строки ведомости </summary>
        private void VedomostEdit_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;

            // Создаём список элементов ведомости, хранящийся в оперативной памяти для ускорения работы программы:
            int vedomostLength = project.GetVedomostLength(vedomostTempSave.GetCurrent());
            List<VedomostItem> tempVedomostList = new List<VedomostItem>(vedomostLength);

            for (int i = 1; i <= vedomostLength; i++)
            {
                tempVedomostList.Add(project.GetVedomostItem(i, vedomostTempSave.GetCurrent()));
            }

            int strNum = id.getStrNum((b.CommandParameter as VedomostItem).id);


            using (EditVedomostItemWindow editWindow = new EditVedomostItemWindow(projectPath, strNum, vedomostTempSave.GetCurrent(), vedomostTempSave.GetCurrent() + 1)) { 
                if (editWindow.ShowDialog() == true)
                {
                    project.DeleteVedomostTempData(vedomostTempSave.SetNext()); // Увеличиваем номер текущего сохранения и одновременно удаляем все последующие сохранения

                    project.BeginTransaction();
                    try {
                        for (int i = 1; i <= vedomostLength; i++)
                        {
                            if (i != strNum)
                            {
                                tempVedomostList[i - 1].id = id.makeID(i, vedomostTempSave.GetCurrent());
                                project.AddVedomostItem(tempVedomostList[i - 1]);
                            }

                        }

                        tempVedomostList[strNum - 1] = project.GetVedomostItem(strNum, vedomostTempSave.GetCurrent());
                        project.AddVedomostItem(tempVedomostList[strNum - 1]);
                    } finally {
                        project.Commit();
                    }

                    DisplayVedomostValues(tempVedomostList);
                }
            }
        }

        /// <summary> Добавление к ведомости пустой строки сверху </summary>
        private void VedomostAdd_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as VedomostItem).id);
            int length = project.GetVedomostLength(vedomostTempSave.GetCurrent());
            List<VedomostItem> vedomostList = new List<VedomostItem>();

            int prevTempSave = vedomostTempSave.GetCurrent();
            project.DeleteVedomostTempData(vedomostTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = vedomostTempSave.SetNext(); // Увеличиваем номер текущего сохранения

            for (int i = 1; i <= length + 1; i++)
            {
                VedomostItem vedomostItem = new VedomostItem();

                if (i < strNum)
                {
                    vedomostItem = project.GetVedomostItem(i, prevTempSave);
                    vedomostItem.id = id.makeID(i, currentTempSave);
                    vedomostList.Add(vedomostItem);
                }
                else if (i == strNum)
                {
                    vedomostItem.id = id.makeID(i, currentTempSave);
                    vedomostList.Add(vedomostItem);
                }
                else if (i > strNum)
                {
                    vedomostItem = project.GetVedomostItem(i - 1, prevTempSave);
                    vedomostItem.id = id.makeID(i, currentTempSave);
                    vedomostList.Add(vedomostItem);
                }

            }

            project.BeginTransaction();
            try {
                for (int i = 0; i < vedomostList.Count; i++) project.AddVedomostItem(vedomostList[i]);
            } finally {
                project.Commit();
            }
            DisplayVedomostValues(vedomostList);
        }

        /// <summary> Удаление из ведомости текущей строки </summary>
        private void VedomostDelete_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as VedomostItem).id);
            int length = project.GetVedomostLength(vedomostTempSave.GetCurrent());
            List<VedomostItem> vedomostList = new List<VedomostItem>();

            int prevTempSave = vedomostTempSave.GetCurrent();
            project.DeleteVedomostTempData(vedomostTempSave.GetCurrent()); // Удаляем все последующие сохранения
            vedomostTempSave.SetNext(); // Увеличиваем номер текущего сохранения
            int currentTempSave = vedomostTempSave.GetCurrent();

            for (int i = 1; i <= length; i++)
            {
                VedomostItem vedomostItem = new VedomostItem();

                if (i < strNum)
                {
                    vedomostItem = project.GetVedomostItem(i, prevTempSave);
                    vedomostItem.id = id.makeID(i, currentTempSave);
                    vedomostList.Add(vedomostItem);
                }
                else if (i > strNum)
                {
                    vedomostItem = project.GetVedomostItem(i, prevTempSave);
                    vedomostItem.id = id.makeID(i - 1, currentTempSave);
                    vedomostList.Add(vedomostItem);
                }

            }
            project.BeginTransaction();
            try {
                for (int i = 0; i < vedomostList.Count; i++) project.AddVedomostItem(vedomostList[i]);
            } finally {
                project.Commit();
            }
            DisplayVedomostValues(vedomostList);
        }

        /// <summary> Правка текущей строки спецификации </summary>
        private void PcbSpecEdit_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;

            // Создаём список элементов перечня, хранящийся в оперативной памяти для ускорения работы программы:
            int length = project.GetPcbSpecLength(pcbSpecTempSave.GetCurrent());
            List<PcbSpecificationItem> tempList = new List<PcbSpecificationItem>(length);

            for (int i = 1; i <= length; i++)
            {
                tempList.Add(project.GetPcbSpecItem(i, pcbSpecTempSave.GetCurrent()));
            }

            int strNum = id.getStrNum((b.CommandParameter as PcbSpecificationItem).id);
            int tempNum = pcbSpecTempSave.GetCurrent();

            using (EditPcbSpecItemWindow editWindow = new EditPcbSpecItemWindow(projectPath, strNum, tempNum, pcbSpecTempSave.GetCurrent() + 1)) { 
                if (editWindow.ShowDialog() == true)
                {
                    project.DeletePcbSpecTempData(pcbSpecTempSave.SetNext()); // Увеличиваем номер текущего сохранения и одновременно удаляем все последующие сохранения               
                    project.BeginTransaction();
                    try {
                        for (int i = 1; i <= length; i++)
                        {
                            if (i != strNum)
                            {
                                tempList[i - 1].id = id.makeID(i, pcbSpecTempSave.GetCurrent());
                                project.AddPcbSpecItem(tempList[i - 1]);
                            }

                        }
                    } finally {
                        project.Commit();
                    }
                    tempList[strNum - 1] = project.GetPcbSpecItem(strNum, pcbSpecTempSave.GetCurrent());

                    DisplayPcbSpecValues(tempList);
                }
            }
        }

        /// <summary>
        /// Возвращает номер строки элемента, следующего за последним элементом в разделе спецификации
        /// Используется при добавлении строки спецификации снизу
        /// </summary>
        /// <param name="section">Раздел спецификации</param>
        private int GetPcbSpecNextNumInSection(Global.SpSections section)
        {
            int strNum = 1;

            List<PcbSpecificationItem> sData = project.GetPcbSpecificationList(pcbSpecTempSave.GetCurrent());
            for (int i = (int)Global.SpSections.Documentation; i <= (int)section; i++)
                strNum += (sData.Where(x => x.spSection == i)).Count();

            return strNum;
        }

        /// <summary> Добавление к спецификации пустой строки сверху </summary>
        private void PcbSpecAdd_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum;

            int pcbSpecSection = 0;

            if (b.Tag as string == "Документация")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Documentation);
                pcbSpecSection = (int)Global.SpSections.Documentation;
            }
            else
            if (b.Tag as string == "Комплексы")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Compleksi);
                pcbSpecSection = (int)Global.SpSections.Compleksi;
            }
            else
            if (b.Tag as string == "Сборочные единицы")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.SborEd);
                pcbSpecSection = (int)Global.SpSections.SborEd;
            }
            else
            if (b.Tag as string == "Детали")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Details);
                pcbSpecSection = (int)Global.SpSections.Details;
            }
            else
            if (b.Tag as string == "Стандартные изделия")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Standard);
                pcbSpecSection = (int)Global.SpSections.Standard;
            }
            else
            if (b.Tag as string == "Прочие изделия")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Other);
                pcbSpecSection = (int)Global.SpSections.Other;
            }
            else
            if (b.Tag as string == "Материалы")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Materials);
                pcbSpecSection = (int)Global.SpSections.Materials;
            }
            else
            if (b.Tag as string == "Комплекты")
            {
                //Определяем номер добавляемой строки
                strNum = GetPcbSpecNextNumInSection(Global.SpSections.Compleсts);
                pcbSpecSection = (int)Global.SpSections.Compleсts;
            }
            else
            {
                strNum = id.getStrNum((b.CommandParameter as PcbSpecificationItem).id);
                pcbSpecSection = (b.CommandParameter as PcbSpecificationItem).spSection;
            }

            int length = project.GetPcbSpecLength(pcbSpecTempSave.GetCurrent());
            List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

            int prevTempSave = pcbSpecTempSave.GetCurrent();
            project.DeletePcbSpecTempData(pcbSpecTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = pcbSpecTempSave.SetNext(); // Увеличиваем номер текущего сохранения 

            for (int i = 1; i <= length + 1; i++)
            {
                PcbSpecificationItem pcbSpecItem = new PcbSpecificationItem();

                if (i < strNum)
                {
                    pcbSpecItem = project.GetPcbSpecItem(i, prevTempSave);
                    pcbSpecItem.id = id.makeID(i, currentTempSave);
                }
                else if (i == strNum)
                {
                    pcbSpecItem = new PcbSpecificationItem();
                    pcbSpecItem.id = id.makeID(i, currentTempSave);
                    pcbSpecItem.spSection = pcbSpecSection;
                    pcbSpecItem.zona = String.Empty;
                    pcbSpecItem.position = String.Empty;
                    pcbSpecItem.oboznachenie = String.Empty;
                    pcbSpecItem.name = String.Empty;
                    pcbSpecItem.quantity = String.Empty;
                    pcbSpecItem.note = String.Empty;
                }
                else if (i > strNum)
                {
                    pcbSpecItem = project.GetPcbSpecItem(i - 1, prevTempSave);
                    pcbSpecItem.id = id.makeID(i, currentTempSave);
                }
                pcbSpecList.Add(pcbSpecItem);
            }
            project.BeginTransaction();
            try {
                for (int i = 0; i < pcbSpecList.Count; i++) project.AddPcbSpecItem(pcbSpecList[i]);
            } finally {
                project.Commit();
            }
            DisplayPcbSpecValues(pcbSpecList);
        }

        /// <summary> Удаление текущей строки спецификации </summary>
        private void PcbSpecDelete_Click(object sender, RoutedEventArgs e)
        {
            Button b = sender as Button;
            int strNum = id.getStrNum((b.CommandParameter as PcbSpecificationItem).id);
            int length = project.GetPcbSpecLength(pcbSpecTempSave.GetCurrent());
            List<PcbSpecificationItem> pcbSpecList = new List<PcbSpecificationItem>();

            int prevTempSave = pcbSpecTempSave.GetCurrent();
            project.DeletePcbSpecTempData(pcbSpecTempSave.GetCurrent()); // Удаляем все последующие сохранения
            int currentTempSave = pcbSpecTempSave.SetNext(); // Увеличиваем номер текущего сохранения

            for (int i = 1; i <= length; i++)
            {
                PcbSpecificationItem pcbSpecItem = new PcbSpecificationItem();

                if (i < strNum)
                {
                    pcbSpecItem = project.GetPcbSpecItem(i, prevTempSave);
                    pcbSpecItem.id = id.makeID(i, currentTempSave);
                }
                else if (i > strNum)
                {
                    pcbSpecItem = project.GetPcbSpecItem(i, prevTempSave);
                    pcbSpecItem.id = id.makeID(i - 1, currentTempSave);
                }
                if (i != strNum) pcbSpecList.Add(pcbSpecItem);

            }
            project.BeginTransaction();
            try {
                for (int i = 0; i < pcbSpecList.Count; i++) project.AddPcbSpecItem(pcbSpecList[i]);
            } finally {
                project.Commit();
            }
            DisplayPcbSpecValues(pcbSpecList);
        }


        /// <summary> Сохранение проекта </summary>
        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (saveProjectMenuItem.IsEnabled == true)
            {
                project.Save(perTempSave.GetCurrent(), specTempSave.GetCurrent(), vedomostTempSave.GetCurrent(), pcbSpecTempSave.GetCurrent());

                project.BeginTransaction();
                try {
                    perTempSave.ProjectSaved();
                    specTempSave.ProjectSaved();
                    pcbSpecTempSave.ProjectSaved();
                    vedomostTempSave.ProjectSaved();

                    //Записываем состояние галочек параметров экспорта в pdf
                    ParameterItem parameterItem = new ParameterItem();
                    parameterItem.name = "isListRegistrChecked";
                    if (addListRegistrCheckBox.IsChecked == true) parameterItem.value = "true";
                    else parameterItem.value = "false";
                    project.SaveParameterItem(parameterItem);

                    parameterItem.name = "isStartFromSecondChecked";
                    if (startFromSecondCheckBox.IsChecked == true) parameterItem.value = "true";
                    else parameterItem.value = "false";
                    project.SaveParameterItem(parameterItem);


                    parameterItem.name = "isPcbMultilayer";
                    if (isPcbMultilayer == true) parameterItem.value = "true";
                    else parameterItem.value = "false";
                    project.SaveParameterItem(parameterItem);
                } finally {
                    project.Commit();
                }
            }
        }

        /// <summary> Отмена действия </summary>
        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (undoMenuItem.IsEnabled == true)
            {
                if (perechenListView.Visibility == Visibility.Visible) perTempSave.SetPrevIfExist();
                if (specificationTabControl.Visibility == Visibility.Visible) specTempSave.SetPrevIfExist();
                if (pcbSpecificationTabControl.Visibility == Visibility.Visible) pcbSpecTempSave.SetPrevIfExist();
                if (vedomostListView.Visibility == Visibility.Visible) vedomostTempSave.SetPrevIfExist();
                DisplayAllValues();
            }
        }

        /// <summary> Возврат действия </summary>
        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            if (redoMenuItem.IsEnabled == true)
            {
                if (perechenListView.Visibility == Visibility.Visible) perTempSave.SetNextIfExist();
                if (specificationTabControl.Visibility == Visibility.Visible) specTempSave.SetNextIfExist();
                if (pcbSpecificationTabControl.Visibility == Visibility.Visible) pcbSpecTempSave.SetNextIfExist();
                if (vedomostListView.Visibility == Visibility.Visible) vedomostTempSave.SetNextIfExist();
                DisplayAllValues();
            }

        }

        /// <summary> Выход из программы </summary>
        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        /// <summary> Открытие справки </summary>
        private void helpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.электроника-и-программирование.рф/DocGOST.html");
        }
    }
}

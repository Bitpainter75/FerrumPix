Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports FerrumPix.Models

Namespace ViewModels

    ''' <summary>
    ''' Der EINE Bauplan aller Kontextmenues. Er bekommt den Aufrufort, die betroffenen Elemente und
    ''' den Zustand des Bereichs und liefert die erlaubten Aktionen.
    '''
    ''' Vorher gab es fuenf ausgeschriebene Menues und 112 Zeilen Sichtbarkeitslogik, die auf
    ''' benannte Eintraege zugriffen. Beschriftungen liefen auseinander, Symbole fehlten mal hier
    ''' mal dort, und eine Aktion aus dem Filmstreifen traf das falsche Bild. Die Regeln stehen
    ''' jetzt einmal, nachlesbar in Audits/KONTEXTMENUE.md.
    '''
    ''' Fuehrende Vorlage ist die GALERIE: ihre Regeln sind erprobt, alles andere leitet sich ab.
    ''' </summary>
    Public NotInheritable Class ContextMenuBuilder

        Private Sub New()
        End Sub

        ''' <param name="ort">Wo geklickt wurde.</param>
        ''' <param name="elemente">Die betroffenen Elemente. In der Galerie die Auswahl, sonst das
        ''' eine gemeinte Bild. Leer heisst: nur die bildunabhaengigen Eintraege.</param>
        ''' <param name="istVirtuell">Suchliste oder Immich - es gibt keinen echten Ordner.</param>
        ''' <param name="kannEinfuegen">Darf in den aktuellen Ordner eingefuegt werden.</param>
        Public Shared Function Build(site As MenuSite,
                                     items As IList(Of ImageItem),
                                     isVirtual As Boolean,
                                     canPaste As Boolean,
                                     commands As MenuCommands) As IReadOnlyList(Of Object)

            Dim list As New List(Of Object)()
            If commands Is Nothing Then Return list

            Dim entries = If(items, New List(Of ImageItem)()).Where(Function(i) i IsNot Nothing).ToList()
            Dim first = entries.FirstOrDefault()
            Dim images = entries.Where(Function(i) i.IsImage).ToList()

            ' Die Zustandsgroessen aus Audits/KONTEXTMENUE.md, eins zu eins.
            Dim isParentEntry = first IsNot Nothing AndAlso first.IsParentFolderEntry
            Dim isSingle = entries.Count = 1
            Dim singleItemActions = isSingle AndAlso Not isParentEntry
            Dim imageBatch = Not isParentEntry AndAlso images.Count > 0

            ' Bildgroesse, Wasserzeichen und Filter fragen genau die Funktion, die auch beim
            ' Ausfuehren entscheidet. Eine eigene Ausschlussliste lief frueher auseinander.
            Dim showResize = imageBatch AndAlso
                               images.All(Function(i) GalleryViewModel.IsBatchImageEditReadable(i.FilePath))
            Dim showExport = imageBatch AndAlso
                              images.Any(Function(i) GalleryViewModel.IsBatchExportable(i.FilePath))
            ' Immich ist hier erlaubt: die Collage holt sich die Originale vorher in den
            ' Temp-Ordner, genau wie das Drucken. Vorher war der Eintrag sichtbar und tat nichts.
            Dim showCollage = entries.Count >= 2 AndAlso images.Count >= 2
            Dim showCompare = entries.Count = 2 AndAlso
                                 entries.All(Function(i) i.IsImage AndAlso Not i.IsImmichAsset AndAlso
                                                        Not String.IsNullOrEmpty(i.FilePath))
            ' Umbenennen: bei Mehrfachauswahl muessen ALLE koennen, bei Einzelauswahl nur das eine.
            Dim showRename = Not isVirtual AndAlso Not isParentEntry AndAlso entries.Count > 0 AndAlso
                                  If(entries.Count > 1,
                                     entries.All(Function(i) Not i.IsParentFolderEntry AndAlso i.CanFileOperationRename),
                                     first.CanFileOperationRename)
            ' Ein Video kann fast nichts davon: alles, was auf Bildpunkten arbeitet, faellt weg.
            Dim videoOnly = entries.Count > 0 AndAlso entries.All(Function(i) i.IsVideoFile)
            ' Nur Ordner angeklickt: "Neues Bild" und "Vollbild" gehoeren nicht in dieses Menue. Sie
            ' haben mit dem Ordner nichts zu tun, und wer einen Ordner anklickt, meint den Ordner.
            ' Bei LEERER Auswahl bleiben sie: dort ist das Menue das der Ansicht.
            Dim folderOnly = entries.Count > 0 AndAlso entries.All(Function(i) i.IsFolder OrElse i.IsParentFolderEntry)

            ' --- ohne Bild ---------------------------------------------------------------------
            ' "Neues Bild" braucht keins - es legt ja erst eines an. "Vollbild" dagegen zeigt ein
            ' Bild gross; ohne eines ist der Eintrag sinnlos.
            If Not folderOnly Then
                AddIfOffered(list, commands.NewImage, FooterMenuCatalog.NewImage(commands.NewImage))
                If imageBatch Then
                    AddIfOffered(list, commands.Fullscreen, FooterMenuCatalog.Fullscreen(commands.Fullscreen))
                End If
                Divider(list)
            End If

            ' --- oeffnen -----------------------------------------------------------------------
            If singleItemActions Then AddIfOffered(list, commands.ShowImage, FooterMenuCatalog.ShowImage(commands.ShowImage))
            If site.ShowsAdjust() AndAlso singleItemActions AndAlso Not videoOnly AndAlso
               first IsNot Nothing AndAlso first.CanEditFile AndAlso first.IsImage Then
                AddIfOffered(list, commands.Adjust, FooterMenuCatalog.Adjust(commands.Adjust))
            End If
            If showCompare Then AddIfOffered(list, commands.Compare, FooterMenuCatalog.Compare(commands.Compare))
            If Not videoOnly Then AddIfOffered(list, commands.PinImage, FooterMenuCatalog.PinImage(commands.PinImage))
            AddIfOffered(list, commands.Save, FooterMenuCatalog.Save(commands.Save))
            AddIfOffered(list, commands.SaveAs, FooterMenuCatalog.SaveAs(commands.SaveAs))
            Divider(list)

            ' --- Dateiarbeit -------------------------------------------------------------------
            If Not isParentEntry AndAlso Not isVirtual AndAlso canPaste Then
                AddIfOffered(list, commands.NewFolder, FooterMenuCatalog.NewFolder(commands.NewFolder))
            End If
            If showRename Then AddIfOffered(list, commands.Rename, FooterMenuCatalog.Rename(commands.Rename))
            If Not isParentEntry AndAlso first IsNot Nothing AndAlso first.CanFileOperationCopy Then
                AddIfOffered(list, commands.Copy, FooterMenuCatalog.Copy(commands.Copy))
            End If
            If Not isParentEntry AndAlso first IsNot Nothing AndAlso first.CanFileOperationRename Then
                AddIfOffered(list, commands.Cut, FooterMenuCatalog.Cut(commands.Cut))
            End If
            If Not isVirtual AndAlso first IsNot Nothing AndAlso first.CanFileOperationPasteInto Then
                AddIfOffered(list, commands.Paste, FooterMenuCatalog.Paste(commands.Paste))
            End If
            If Not isVirtual AndAlso Not isParentEntry AndAlso first IsNot Nothing AndAlso first.CanFileOperationCopy Then
                AddIfOffered(list, commands.Duplicate, FooterMenuCatalog.Duplicate(commands.Duplicate))
            End If
            Divider(list)

            ' --- Stapelarbeit am Bild ----------------------------------------------------------
            If showResize Then
                AddIfOffered(list, commands.ResizeImage, FooterMenuCatalog.ResizeImage(commands.ResizeImage))
                AddIfOffered(list, commands.ApplyWatermark, FooterMenuCatalog.ApplyWatermark(commands.ApplyWatermark))
                AddIfOffered(list, commands.ApplyFilter, FooterMenuCatalog.ApplyFilter(commands.ApplyFilter))
            End If
            If showExport Then
                AddIfOffered(list, commands.ConvertTo, FooterMenuCatalog.ConvertTo(commands.ConvertTo))
                AddIfOffered(list, commands.ExportTo, FooterMenuCatalog.ExportTo(commands.ExportTo))
            End If
            ' Metadaten entfernen schreibt die DATEI neu. Ein Immich-Asset hat keine, die wir
            ' anfassen koennten - dort taete der Eintrag nichts. Genau das fragt CanRemoveMetadata,
            ' und genau daran hing der Eintrag frueher schon: als Bindung im Menue-XAML. Beim
            ' Umbau ging sie verloren, weil die Regeln aus der Sichtbarkeits-LOGIK gezogen wurden.
            If imageBatch AndAlso images.All(Function(i) i.CanRemoveMetadata) Then
                AddIfOffered(list, commands.RemoveMetadata, FooterMenuCatalog.RemoveMetadata(commands.RemoveMetadata))
            End If
            If imageBatch Then
                AddIfOffered(list, commands.Print, FooterMenuCatalog.Print(commands.Print))
            End If
            If showCollage Then AddIfOffered(list, commands.CreateCollage, FooterMenuCatalog.CreateCollage(commands.CreateCollage))
            If imageBatch AndAlso Not videoOnly Then
                AddIfOffered(list, commands.RotateLeft, FooterMenuCatalog.RotateLeft(commands.RotateLeft))
                AddIfOffered(list, commands.RotateRight, FooterMenuCatalog.RotateRight(commands.RotateRight))
            End If
            Divider(list)

            ' --- Bewertung, Favorit, Etikett ---------------------------------------------------
            ' Keine Untermenues: Sterne, Herz und Farbkreise kommen als eigene Zeilen ins Menue.
            ' Der Zustand wird aus den betroffenen Bildern abgelesen und nur angezeigt, wenn ALLE
            ' denselben haben - bei gemischter Auswahl waere jede Anzeige gelogen.
            If imageBatch Then
                Dim ratingRow = Controls.MenuWidgets.RatingRow(CommonRating(items),
                                                              CommonFavorite(items),
                                                              commands.Rating, commands.Favorite)
                If ratingRow IsNot Nothing Then list.Add(ratingRow)
                Dim labelRow = Controls.MenuWidgets.ColorLabelRow(CommonColorLabel(items), commands.ColorLabel)
                If labelRow IsNot Nothing Then list.Add(labelRow)
                Divider(list)
            End If

            ' --- Wege nach draussen ------------------------------------------------------------
            ' Beides zeigt auf einen Ort im Dateisystem. Ein Immich-Asset hat keinen, und in einer
            ' Suchliste stehen die Treffer quer ueber den Bestand verstreut - der Sprung dorthin
            ' fuehrt aus der Trefferliste heraus und ist nicht das, was man beim Klick erwartet.
            '
            ' OHNE Auswahl bleiben beide: dann meinen sie den offenen Ordner. Ein Menue, in dem
            ' nichts steht, weil gerade nichts markiert ist, hilft niemandem - es gibt genug, was
            ' auch ohne Bild geht.
            If (singleItemActions OrElse entries.Count = 0) AndAlso Not isVirtual AndAlso
               (first Is Nothing OrElse Not first.IsImmichAsset) Then
                AddIfOffered(list, commands.CopyPath, FooterMenuCatalog.CopyPath(commands.CopyPath))
                AddIfOffered(list, commands.ShowInFileManager, FooterMenuCatalog.ShowInFileManager(commands.ShowInFileManager))
                Divider(list)
            End If

            If Not isParentEntry AndAlso first IsNot Nothing AndAlso first.CanFileOperationDelete Then
                AddIfOffered(list, commands.Delete, FooterMenuCatalog.Delete(commands.Delete))
            End If

            TidySeparators(list)
            Return list
        End Function

        ''' Nimmt den Eintrag nur auf, wenn der Bereich das Kommando ueberhaupt anbietet.
        Private Shared Sub AddIfOffered(list As List(Of Object), command As Object, entry As AppAction)
            If command IsNot Nothing Then list.Add(entry)
        End Sub

        Private Shared Sub Divider(list As List(Of Object))
            list.Add(FooterMenuCatalog.Divider())
        End Sub

        ''' <summary>Die Bewertung, wenn ALLE betroffenen Bilder dieselbe haben, sonst keine.
        ''' Bei gemischter Auswahl waere jede angezeigte Zahl gelogen.</summary>
        Private Shared Function CommonRating(items As IList(Of ImageItem)) As Integer
            If items Is Nothing OrElse items.Count = 0 Then Return 0
            Dim first = items(0).Rating
            For Each item In items
                If item Is Nothing OrElse item.Rating <> first Then Return 0
            Next
            Return first
        End Function

        ''' <summary>Das Etikett, wenn ALLE betroffenen Bilder dasselbe tragen, sonst keines.</summary>
        Private Shared Function CommonColorLabel(items As IList(Of ImageItem)) As String
            If items Is Nothing OrElse items.Count = 0 Then Return ""
            Dim first = If(items(0)?.ColorLabel, "")
            For Each item In items
                If item Is Nothing OrElse Not String.Equals(If(item.ColorLabel, ""), first, StringComparison.OrdinalIgnoreCase) Then Return ""
            Next
            Return first
        End Function

        ''' <summary>Favorit nur, wenn ALLE betroffenen Bilder einer sind.</summary>
        Private Shared Function CommonFavorite(items As IList(Of ImageItem)) As Boolean
            If items Is Nothing OrElse items.Count = 0 Then Return False
            For Each item In items
                If item Is Nothing OrElse Not item.IsFavorite Then Return False
            Next
            Return True
        End Function

        ''' <summary>Trenner am Anfang, am Ende und doppelte entfernen. Ohne das haengen sie
        ''' herum, sobald eine ganze Gruppe wegfaellt - und das passiert staendig, weil fast jede
        ''' Gruppe an einer Bedingung haengt.</summary>
        Private Shared Sub TidySeparators(list As List(Of Object))
            Dim isDivider = Function(o As Object) TypeOf o Is Avalonia.Controls.Separator

            While list.Count > 0 AndAlso isDivider(list(0))
                list.RemoveAt(0)
            End While
            While list.Count > 0 AndAlso isDivider(list(list.Count - 1))
                list.RemoveAt(list.Count - 1)
            End While
            Dim i = list.Count - 1
            While i > 0
                If isDivider(list(i)) AndAlso isDivider(list(i - 1)) Then list.RemoveAt(i)
                i -= 1
            End While
        End Sub

    End Class

End Namespace

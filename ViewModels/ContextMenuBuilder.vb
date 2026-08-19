Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports FerrumPix.Models
Imports FerrumPix.Services

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

        ''' <param name="place">Wo geklickt wurde.</param>
        ''' <param name="items">Die betroffenen Elemente. In der Galerie die Auswahl, sonst das
        ''' eine gemeinte Bild. Leer heisst: nur die bildunabhaengigen Eintraege.</param>
        ''' <param name="isVirtual">Suchliste oder Immich - es gibt keinen echten Ordner.</param>
        ''' <param name="canPaste">Darf in den aktuellen Ordner eingefuegt werden.</param>
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
            ' Vergleichen geht ueber den lokalen Weg des Betrachters, und der bekommt DATEIPFADE.
            ' Ein Pseudo-Pfad scheitert dort - geprueft wird deshalb auf JEDE Serverquelle und
            ' nicht nur auf Immich. Vorher boten zwei markierte Nextcloud-Bilder den Eintrag an,
            ' und der Klick lief ins Leere.
            Dim showCompare = entries.Count = 2 AndAlso
                                 entries.All(Function(i) i.IsImage AndAlso Not i.IsRemoteAsset AndAlso
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

            ' --- Papierkorb: eine Ansicht, eine Geste -------------------------------------------
            ' Was im Papierkorb einer Serverquelle liegt, ist keine gewoehnliche Aufnahme mehr:
            ' bearbeiten, umbenennen, exportieren und erst recht loeschen ergeben dort nichts.
            ' Angeboten wird deshalb genau das Zurueckholen - und das Ansehen, damit man vorher
            ' erkennt, was man zurueckholt.
            Dim trashed = entries.Count > 0 AndAlso entries.All(Function(i) i.IsTrashed)
            If trashed Then
                If singleItemActions Then AddIfOffered(list, commands.ShowImage, FooterMenuCatalog.ShowImage(commands.ShowImage))
                AddIfOffered(list, commands.RestoreFromTrash, FooterMenuCatalog.RestoreFromTrash(commands.RestoreFromTrash))
                TidySeparators(list)
                Return list
            End If

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
                ' Nur die BILDER ablesen, nicht die ganze Markierung: ein mitmarkierter Ordner
                ' traegt weder Sterne noch Etikett und drueckte die Zeile sonst auf leer.
                Dim ratingRow = Controls.MenuWidgets.RatingRow(CommonRating(images),
                                                              CommonFavorite(images),
                                                              commands.Rating, commands.Favorite)
                If ratingRow IsNot Nothing Then list.Add(ratingRow)
                Dim labelRow = Controls.MenuWidgets.ColorLabelRow(CommonColorLabel(images), commands.ColorLabel)
                If labelRow IsNot Nothing Then list.Add(labelRow)
                Divider(list)
            End If

            ' --- Metadaten (Untermenue) --------------------------------------------------------
            ' Aufnahmeort und "Metadaten entfernen" arbeiten beide an den Angaben ZUR Aufnahme und
            ' gehoeren zusammen. Einzeln im Hauptmenue waeren es vier weitere Zeilen in einer
            ' Liste, die ohnehin lang ist.
            Dim metadataChildren = BuildMetadataChildren(images, isSingle, isParentEntry, imageBatch, commands)
            If metadataChildren.Count > 0 Then
                list.Add(FooterMenuCatalog.Metadata(metadataChildren))
                Divider(list)
            End If

            ' --- Wege nach draussen ------------------------------------------------------------
            ' Beides zeigt auf einen Ort im Dateisystem. Ein SERVERELEMENT hat keinen - gleich von
            ' welchem Server -, und in einer Suchliste stehen die Treffer quer ueber den Bestand
            ' verstreut; der Sprung dorthin fuehrt aus der Trefferliste heraus und ist nicht das,
            ' was man beim Klick erwartet.
            '
            ' OHNE Auswahl bleiben beide: dann meinen sie den offenen Ordner. Ein Menue, in dem
            ' nichts steht, weil gerade nichts markiert ist, hilft niemandem - es gibt genug, was
            ' auch ohne Bild geht.
            If (singleItemActions OrElse entries.Count = 0) AndAlso Not isVirtual AndAlso
               (first Is Nothing OrElse Not first.IsRemoteAsset) Then
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

        ''' <summary>Die Eintraege des Untermenues "Metadaten". Leer heisst: das Untermenue faellt
        ''' ganz weg - ein Menuepunkt, der aufklappt und nichts zeigt, ist schlechter als keiner.
        '''
        ''' Der Aufnahmeort geht in Datei, Beistelldatei und Katalog (siehe GeotagService). Ein
        ''' SERVERBILD hat keine Datei, die ihn tragen koennte, und sein Katalogeintrag haengt an
        ''' einem Pseudo-Pfad - der Ort bliebe dort haengen und erreichte nie das Bild. Deshalb
        ''' zaehlen hier nur lokale Bilder.</summary>
        Private Shared Function BuildMetadataChildren(images As IList(Of ImageItem),
                                                      isSingle As Boolean,
                                                      isParentEntry As Boolean,
                                                      imageBatch As Boolean,
                                                      commands As MenuCommands) As IReadOnlyList(Of Object)
            Dim children As New List(Of Object)()
            If isParentEntry Then Return children

            Dim localImages = images.Where(Function(i) Not i.IsRemoteAsset AndAlso
                                                       Not String.IsNullOrEmpty(i.FilePath)).ToList()
            If localImages.Count > 0 Then
                ' KOPIEREN nur, wo es etwas zu kopieren gibt: genau ein Bild, und das mit
                ' Koordinate. Der Blick in den Katalog kostet eine Abfrage beim Rechtsklick - ein
                ' sichtbarer Eintrag, der nichts tut, kostet mehr.
                If isSingle AndAlso localImages.Count = 1 Then
                    Dim stored = LibraryService.Instance.GetGpsCoordinates(localImages(0).FilePath)
                    If stored.Latitude.HasValue AndAlso stored.Longitude.HasValue Then
                        AddIfOffered(children, commands.CopyPlace, FooterMenuCatalog.CopyPlace(commands.CopyPlace))
                        ' Die Karte lohnt nur fuer EIN Bild mit Ort - bei einer Auswahl waere
                        ' unklar, wessen Ort sie zeigt. Dieselbe Bedingung wie beim Kopieren.
                        AddIfOffered(children, commands.OpenPlaceInOsm,
                                     FooterMenuCatalog.OpenPlaceInOsm(commands.OpenPlaceInOsm))
                    End If
                End If
                ' EINFUEGEN und SETZEN gelten fuer die ganze Auswahl - der Regelfall ist eine Reihe
                ' Aufnahmen vom selben Ort.
                If GeotagClipboard.HasCoordinate Then
                    AddIfOffered(children, commands.PastePlace,
                                 FooterMenuCatalog.PastePlace(commands.PastePlace, GeotagClipboard.Label))
                End If
                AddIfOffered(children, commands.SetPlace, FooterMenuCatalog.SetPlace(commands.SetPlace))
                ' LOESCHEN nur, wo einer steht - bei einer Auswahl reicht EIN Bild mit Ort. EINE
                ' Abfrage fuer die ganze Auswahl, nicht eine je Bild: bei einem grossen Stapel
                ' waere der Rechtsklick sonst spuerbar traege.
                If LibraryService.Instance.AnyGpsCoordinates(localImages.Select(Function(i) i.FilePath)) Then
                    AddIfOffered(children, commands.RemovePlace, FooterMenuCatalog.RemovePlace(commands.RemovePlace))
                End If

                ' Der Urheberrechtshinweis gilt IMMER fuer die ganze Auswahl und braucht keinen
                ' Blick in den Katalog: er steht in der Datei, nicht bei uns. Ein Trenner davor,
                ' weil er eine andere Angabe meint als der Aufnahmeort darueber.
                children.Add(FooterMenuCatalog.Divider())
                AddIfOffered(children, commands.SetCopyright, FooterMenuCatalog.SetCopyright(commands.SetCopyright))
            End If

            ' Metadaten entfernen schreibt die DATEI neu. Ein Serverbild hat keine, die wir
            ' anfassen koennten - dort taete der Eintrag nichts. Genau das fragt CanRemoveMetadata,
            ' und genau daran hing der Eintrag frueher schon: als Bindung im Menue-XAML. Beim
            ' Umbau ging sie verloren, weil die Regeln aus der Sichtbarkeits-LOGIK gezogen wurden.
            If imageBatch AndAlso images.All(Function(i) i.CanRemoveMetadata) Then
                If children.Count > 0 Then children.Add(FooterMenuCatalog.Divider())
                AddIfOffered(children, commands.RemoveMetadata, FooterMenuCatalog.RemoveMetadata(commands.RemoveMetadata))
            End If

            TidySeparators(children)
            Return children
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

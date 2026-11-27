Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Das Ziel "Herunterladen" der Dialoge im Browser (Speichern unter, Exportieren nach und die
    ''' Stapelfunktionen mit Zielordner), dazu der Dateiwaehler der Seite fuer "Hochladen".
    '''
    ''' <para>EIN EIGENER ORDNER IM DATENORDNER, NICHT IM BESTAND. Wer "Herunterladen" waehlt, will
    ''' die Dateien auf seinem Geraet und nicht im Fotoordner des Servers. Geschrieben wird deshalb
    ''' in einen Ordner unter dem Datenordner (je Rechner benannt, je Lauf und je Auftrag ein
    ''' Unterordner), angeboten wird mit startNow, und der Ordner gehoert dem Angebot: nach dem
    ''' vollstaendigen Download, beim Ablauf des Angebots (zwei Stunden), beim Verdraengen durch neue
    ''' Angebote und beim Anhalten der Bruecke geht er mit, jeweils erst, wenn keine Auslieferung mehr
    ''' aus ihm liest (DownloadOffer.ActiveDeliveries). Was ein Absturz liegen laesst, raeumt der
    ''' naechste Start weg (<see cref="CleanStaleDownloadFolders"/>).</para>
    '''
    ''' <para>Die Pruefung FileOperationPolicy.CanCopy gilt hier nicht: der Datenordner liegt ab Werk
    ''' unter ~/.local/share und damit in einem versteckten Ordner. Angeboten wird dafuer nur, was
    ''' in genau dem Ordner liegt, den die Bruecke selbst angelegt hat.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Der Name des Ordners im Datenordner; in einem gewaehlten Datenordner steht der
        ''' Rechnername dazwischen (AppFolders.MachineSpecificName).</summary>
        Private Const DownloadFolderBaseName As String = "browser-downloads"

        ''' <summary>Der Unterordner dieses Laufs. Das Aufraeumen beim Start laesst ihn stehen, auch
        ''' wenn es erst nach dem ersten Auftrag dazu kommt.</summary>
        Private Shared ReadOnly DownloadRunId As String = Guid.NewGuid().ToString("N")

        ''' <summary>Wie oft abgelaufene Angebote samt Ordner geraeumt werden.</summary>
        Private Shared ReadOnly OfferSweepInterval As TimeSpan = TimeSpan.FromMinutes(5)

        Private Shared ReadOnly OfferSweepLock As New Object()
        Private Shared _offerSweep As Timer

        ''' <summary>Der Ordner fuer alle Auftraege mit dem Ziel "Herunterladen".</summary>
        Friend Shared ReadOnly Property DownloadRoot As String
            Get
                Return AppFolders.InData(AppFolders.MachineSpecificName(DownloadFolderBaseName, ""))
            End Get
        End Property

        ''' <summary>Gibt es das Ziel "Herunterladen"? Nur im Browser und mit laufender Bruecke.</summary>
        Public Shared ReadOnly Property CanDownloadToDevice As Boolean
            Get
                Return IsActive AndAlso _listener IsNot Nothing
            End Get
        End Property

        ''' <summary>Legt einen frischen Ordner fuer einen Auftrag an und liefert ihn, oder Nothing,
        ''' wenn es das Ziel nicht gibt oder der Ordner nicht entsteht.</summary>
        Public Shared Function CreateDownloadFolder() As String
            If Not CanDownloadToDevice Then Return Nothing
            Try
                Dim folder = Path.Combine(DownloadRoot, DownloadRunId, Guid.NewGuid().ToString("N").Substring(0, 12))
                Directory.CreateDirectory(folder)
                Return folder
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("BrowserBridge.Download", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Reicht der Platz im Datenordner grob fuer einen Auftrag? Geschaetzt wird mit der
        ''' Groesse der Quellen; danach muessen wie beim Hochladen 512 MB frei bleiben. Nothing heisst
        ''' ja, sonst steht hier der Satz fuer die Meldung. Ein Laufwerk ohne Auskunft gilt als
        ''' ausreichend; ein volles meldet sich dann beim Schreiben.</summary>
        Public Shared Function DownloadSpaceProblem(folder As String, sources As IEnumerable(Of String)) As String
            Dim expected As Long = 0
            For Each source In If(sources, Enumerable.Empty(Of String)())
                Try
                    Dim sourceFile = VariantKey.FileOf(source)
                    If Not String.IsNullOrEmpty(sourceFile) AndAlso File.Exists(sourceFile) Then expected += New FileInfo(sourceFile).Length
                Catch
                End Try
            Next
            Dim free = FreeSpace(If(folder, DownloadRoot))
            If free - expected >= FreeSpaceReserveBytes Then Return Nothing
            Return LocalizationService.T("Im Datenordner ist nicht genug Platz für den Download frei")
        End Function

        ''' <summary>Bietet an, was ein Auftrag in <paramref name="folder"/> geschrieben hat, und
        ''' startet den Download gleich. Der Ordner geht danach mit dem Angebot. Liegt nichts darin
        ''' (alles fehlgeschlagen oder abgebrochen), wird er gleich geloescht. Liefert die Kennung des
        ''' Angebots oder Nothing.</summary>
        Public Shared Function OfferDownloadFolder(folder As String, files As IEnumerable(Of String)) As String
            If Not IsOwnDownloadFolder(folder) Then Return Nothing
            Dim inside = If(files, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Select(Function(p) VariantKey.FileOf(p)).
                Where(Function(p) File.Exists(p) AndAlso PathIdentity.IsAncestorOrSelf(folder, p)).
                Distinct(PathIdentity.Comparer).ToList()
            If inside.Count = 0 OrElse _listener Is Nothing Then
                DeleteDownloadFolder(folder)
                Return Nothing
            End If
            Return RegisterOffer(inside, startNow:=True, ownedFolder:=folder)
        End Function

        ''' <summary>Verwirft einen Auftragsordner, der nicht angeboten wird (Abbruch vor dem
        ''' Schreiben).</summary>
        Public Shared Sub DiscardDownloadFolder(folder As String)
            DeleteDownloadFolder(folder)
        End Sub

        ''' <summary>Liegt der Ordner unter <see cref="DownloadRoot"/>, ist aber nicht er selbst?
        ''' Geloescht wird nur, was diese Frage besteht. Die Stapelwege fragen sie auch, um das Ziel
        ''' "Herunterladen" von der Grenze der festen Fotoordner auszunehmen.</summary>
        Friend Shared Function IsOwnDownloadFolder(folder As String) As Boolean
            If String.IsNullOrWhiteSpace(folder) Then Return False
            Dim root = DownloadRoot
            Return PathIdentity.IsAncestorOrSelf(root, folder) AndAlso Not PathIdentity.AreSame(root, folder)
        End Function

        Private Shared Sub DeleteDownloadFolder(folder As String)
            If Not IsOwnDownloadFolder(folder) Then Return
            Try
                If Directory.Exists(folder) Then Directory.Delete(folder, recursive:=True)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Eine Datei ist gerade offen (Windows). Der naechste Start raeumt sie weg.
                DiagnosticLogService.LogException("BrowserBridge.Download", ex)
            End Try
        End Sub

        ''' <summary>Ein Angebot mit eigenem Ordner ist ausgeliefert: Ordner und Angebot gehen, und
        ''' die Seite nimmt seinen Eintrag weg, dessen Knopf sonst ins Leere griffe. Laeuft gerade
        ''' noch eine Auslieferung (ein zweiter Abruf, oder das Fegen trifft einen laufenden
        ''' Download), bleibt der Ordner bis zu deren Ende (BrowserBridge.EndDelivery).</summary>
        Private Shared Sub ReleaseOwnedOffer(id As String)
            Dim offer As DownloadOffer = Nothing
            Dim folder As String = Nothing
            SyncLock Offers
                If Not Offers.TryGetValue(id, offer) Then Return
                Offers.Remove(id)
                folder = RetireOffer(offer)
            End SyncLock
            DeleteDownloadFolder(folder)
            NotifyOfferState("gone", id)
        End Sub

        ''' <summary>Raeumt abgelaufene Angebote mit eigenem Ordner. Ohne diesen Lauf ginge ein
        ''' Angebot, das nie geholt wird, erst beim naechsten neuen Angebot.</summary>
        Private Shared Sub SweepOffers()
            Dim expired As List(Of String)
            SyncLock Offers
                expired = Offers.Where(Function(o) o.Value.OwnedFolder IsNot Nothing AndAlso
                                                   DateTime.UtcNow - o.Value.Created > OfferLifetime).
                                 Select(Function(o) o.Key).ToList()
            End SyncLock
            For Each id In expired
                ReleaseOwnedOffer(id)
            Next
        End Sub

        Private Shared Sub EnsureOfferSweep()
            SyncLock OfferSweepLock
                If _offerSweep IsNot Nothing Then Return
                _offerSweep = New Timer(Sub(state) SweepOffers(), Nothing, OfferSweepInterval, OfferSweepInterval)
            End SyncLock
        End Sub

        Private Shared Sub StopOfferSweep()
            SyncLock OfferSweepLock
                _offerSweep?.Dispose()
                _offerSweep = Nothing
            End SyncLock
        End Sub

        ''' <summary>Loescht, was fruehere Laeufe unter <see cref="DownloadRoot"/> liegen liessen.
        ''' Der Ordner dieses Laufs bleibt. Beim Start der Anwendung gerufen (App.StartMainWindow,
        ''' am Rechner wie im Browser), nach einem Umzug des Datenordners und im Hintergrund.</summary>
        Public Shared Sub CleanStaleDownloadFolders()
            Try
                Dim root = DownloadRoot
                If Not Directory.Exists(root) Then Return
                For Each entry In Directory.EnumerateFileSystemEntries(root).ToList()
                    If String.Equals(Path.GetFileName(entry), DownloadRunId, StringComparison.Ordinal) Then Continue For
                    Try
                        If Directory.Exists(entry) Then
                            Directory.Delete(entry, recursive:=True)
                        Else
                            File.Delete(entry)
                        End If
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        DiagnosticLogService.LogException("BrowserBridge.Download", ex)
                    End Try
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("BrowserBridge.Download", ex)
            End Try
        End Sub

        ''' <summary>"Hochladen" in der Galerie: die Seite oeffnet den Dateiwaehler des Browsers.
        '''
        ''' <para>Den Waehler oeffnet ein Browser nur in einer Geste des Nutzers. Der Klick auf den
        ''' Knopf IST eine: er beginnt als pointerdown auf dem Canvas der Seite, und Browser gewaehren
        ''' danach fuer einige Zeit eine voruebergehende Aktivierung (HTML "transient activation",
        ''' Chrome und Firefox rund fuenf Sekunden, Safari rund eine). Bis die Anwendung den Klick
        ''' ausgewertet und diese Nachricht geschickt hat, vergehen Millisekunden. Die Seite ruft
        ''' den Waehler deshalb gleich beim Eintreffen. Meldet der Browser keine Aktivierung mehr
        ''' (navigator.userActivation), zeigt sie statt dessen einen eigenen Knopf, dessen Klick
        ''' wieder eine Geste ist.</para>
        '''
        ''' <para>Verworfen: der Seite vorab die Lage des Knopfs zu schicken und den Waehler im
        ''' Ereignis des Canvas selbst zu oeffnen. Das braeuchte die Lage bei jedem Umbau der
        ''' Oberflaeche und oeffnete den Waehler auch dort, wo die Anwendung den Klick anders
        ''' versteht (ein Dialog darueber, ein Menue).</para>
        '''
        ''' <para>Die Dateien gehen ohne Stelle hinauf und damit in den Ordner der Galerie, wie
        ''' beim Ablegen ausserhalb des Editors.</para>
        '''
        ''' <para>EIN WAEHLER ZUR ZEIT. Solange einer offen ist, geht kein zweites "pick" hinaus;
        ''' die Seite meldet das Ende der Auswahl ("picked", nach einer Wahl wie nach Abbrechen).
        ''' Kommt keins (ein Browser ohne das Ereignis "cancel"), gibt die Zeitgrenze den Weg
        ''' wieder frei. Die Seite selbst verwirft ein "pick", das kurz nach dem Schliessen
        ''' eintrifft. Beides ist Absicherung; die Ursache des Befunds, dass sich der Waehler
        ''' immer wieder oeffnete, steht bei CancelPointer.</para>
        '''
        ''' <para>False ohne laufende Bruecke, ohne verbundenen Browser und bei offenem Waehler.</para></summary>
        Public Shared Function RequestFilePicker() As Boolean
            If _listener Is Nothing OrElse CurrentClient Is Nothing Then Return False
            Dim now = Environment.TickCount64
            Dim openedAt = Interlocked.Read(_filePickerOpenedAt)
            If openedAt <> 0 AndAlso now - openedAt < FilePickerTimeoutMilliseconds Then Return False
            Interlocked.Exchange(_filePickerOpenedAt, now)
            Dim ignored = SendTextAsync("{""t"":""pick""}")
            Return True
        End Function

        ''' <summary>Seit wann ein Dateiwaehler offen ist (Environment.TickCount64), 0 ohne.</summary>
        Private Shared _filePickerOpenedAt As Long

        ''' <summary>So lange gilt ein Dateiwaehler hoechstens als offen, wenn die Seite kein Ende
        ''' meldet. Danach oeffnet der naechste Klick wieder einen.</summary>
        Private Const FilePickerTimeoutMilliseconds As Long = 60000

        ''' <summary>Die Seite meldet das Ende der Auswahl, oder ein neuer Browser hat sich
        ''' verbunden.</summary>
        Private Shared Sub FilePickerClosed()
            Interlocked.Exchange(_filePickerOpenedAt, 0)
        End Sub
    End Class
End Namespace

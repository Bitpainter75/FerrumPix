Imports Avalonia.Input
Imports Avalonia.Input.Platform
Imports Avalonia.Platform.Storage
Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks

Namespace Services

    Public Class ClipboardPathData
        Public Sub New()
            Paths = New List(Of String)()
        End Sub

        Public Property Paths As List(Of String)
        Public Property IsCut As Boolean
        Public Property ClipboardWasReadable As Boolean
    End Class

    Public NotInheritable Class ClipboardPathService
        Private Sub New()
        End Sub

        ''' Anwendungseigenes Format, um Ausschneiden (statt Kopieren) über die Zwischenablage
        ''' hinweg zu erkennen - wird zusätzlich zu Avalonias DataFormat.File gesetzt/gelesen.
        Private Shared ReadOnly CutFormat As DataFormat(Of String) = DataFormat.CreateStringApplicationFormat("FerrumPixCut")

        Public Shared Async Function CopyPathsAsync(clipboard As IClipboard, storageProvider As IStorageProvider, paths As IEnumerable(Of String), cut As Boolean) As Task
            If clipboard Is Nothing OrElse paths Is Nothing Then Return

            Dim validPaths = paths.
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            If validPaths.Count = 0 Then Return

            Dim uriList = String.Join(ControlChars.Lf, validPaths.Select(Function(p) New Uri(IO.Path.GetFullPath(p)).AbsoluteUri))

            Dim transfer = Await BuildFileTransferAsync(storageProvider, validPaths,
                Sub(firstItem)
                    firstItem.SetText(uriList)
                    firstItem.Set(CutFormat, If(cut, "1", "0"))
                End Sub)

            If transfer.Items.Count = 0 Then
                Await clipboard.SetTextAsync(uriList)
                Return
            End If

            Dim setDataFailed = False
            Try
                Await clipboard.SetDataAsync(transfer)
            Catch
                setDataFailed = True
            End Try
            If setDataFailed Then Await clipboard.SetTextAsync(uriList)
        End Function

        ''' <summary>Baut einen DataTransfer mit einem Datei-Eintrag je Pfad. DataFormat.File ist Avalonias
        ''' plattformübergreifendes Dateiformat und wird intern auf CF_HDROP unter Windows, text/uri-list
        ''' unter Linux und NSFilenamesPboardType unter macOS abgebildet - nur damit können Dolphin, Nautilus
        ''' oder der Windows-Explorer die Daten annehmen, egal ob sie aus der Zwischenablage oder aus einer
        ''' Ziehen-und-Ablegen-Geste stammen. Ein anwendungseigenes Format allein sehen sie nicht.</summary>
        ''' <param name="decorateFirstItem">Wird auf dem ersten Eintrag aufgerufen, um zusätzliche Formate
        ''' anzuhängen (Text-Darstellung, Ausschneide-Marke, anwendungsinterne Pfadliste).</param>
        Public Shared Async Function BuildFileTransferAsync(storageProvider As IStorageProvider,
                                                           paths As IEnumerable(Of String),
                                                           Optional decorateFirstItem As Action(Of DataTransferItem) = Nothing) As Task(Of DataTransfer)
            Dim transfer = New DataTransfer()
            If paths Is Nothing Then Return transfer

            Dim isFirstItem = True
            For Each path In paths
                Dim storageItem = Await TryResolveStorageItemAsync(storageProvider, path)
                If storageItem Is Nothing Then Continue For

                Dim item = New DataTransferItem()
                item.SetFile(storageItem)
                If isFirstItem Then
                    decorateFirstItem?.Invoke(item)
                    isFirstItem = False
                End If
                transfer.Add(item)
            Next
            Return transfer
        End Function

        ''' Wandelt die Datei-Einträge eines DataTransfers (eigene oder fremde) in lokale Pfade um.
        Public Shared Function ToLocalPaths(items As IEnumerable(Of IStorageItem)) As List(Of String)
            If items Is Nothing Then Return New List(Of String)()
            Return items.
                Select(Function(f) TryGetLocalPath(f)).
                Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso (IO.File.Exists(p) OrElse IO.Directory.Exists(p))).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Private Shared Async Function TryResolveStorageItemAsync(storageProvider As IStorageProvider, path As String) As Task(Of IStorageItem)
            If storageProvider Is Nothing Then Return Nothing
            Try
                Dim uri = New Uri(IO.Path.GetFullPath(path))
                If IO.Directory.Exists(path) Then Return Await storageProvider.TryGetFolderFromPathAsync(uri)
                Return Await storageProvider.TryGetFileFromPathAsync(uri)
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Async Function ReadPathsAsync(clipboard As IClipboard) As Task(Of List(Of String))
            Return (Await ReadPathDataAsync(clipboard)).Paths
        End Function

        Public Shared Async Function ReadPathDataAsync(clipboard As IClipboard) As Task(Of ClipboardPathData)
            If clipboard Is Nothing Then Return New ClipboardPathData()

            Dim viaTransfer = Await TryReadFromDataTransferAsync(clipboard)
            If viaTransfer IsNot Nothing Then Return viaTransfer

            ' Fallback für Zwischenablageninhalte, bei denen DataFormat.File nicht verfügbar ist -
            ' etwa ältere/abweichende MIME-Angebote mancher Programme.
            Try
                Dim formats = Await clipboard.GetDataFormatsAsync()
                For Each dataFormatName In {"x-special/gnome-copied-files", "text/uri-list", "text/plain", "TEXT", DataFormat.Text.Identifier}
                    If formats IsNot Nothing AndAlso Not formats.Any(Function(f) String.Equals(f.Identifier, dataFormatName, StringComparison.OrdinalIgnoreCase)) Then Continue For
                    If String.Equals(dataFormatName, DataFormat.Text.Identifier, StringComparison.Ordinal) OrElse
                       String.Equals(dataFormatName, "text/plain", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(dataFormatName, "TEXT", StringComparison.OrdinalIgnoreCase) Then
                        Dim textData = Await clipboard.TryGetTextAsync()
                        Dim parsed = ParsePathText(textData)
                        If parsed.Paths.Count > 0 Then
                            parsed.ClipboardWasReadable = True
                            Return parsed
                        End If
                    End If
                Next
            Catch
            End Try

            Try
                Dim text = Await clipboard.TryGetTextAsync()
                Dim parsed = ParsePathText(text)
                parsed.ClipboardWasReadable = True
                Return parsed
            Catch
                Return New ClipboardPathData()
            End Try
        End Function

        Private Shared Async Function TryReadFromDataTransferAsync(clipboard As IClipboard) As Task(Of ClipboardPathData)
            Dim transfer As IAsyncDataTransfer = Nothing
            Dim ownsTransfer = False
            Try
                Try
                    transfer = Await clipboard.TryGetInProcessDataAsync()
                Catch
                End Try
                If transfer Is Nothing Then
                    transfer = Await clipboard.TryGetDataAsync()
                    ownsTransfer = True
                End If
                If transfer Is Nothing Then Return Nothing

                Dim files = Await transfer.TryGetFilesAsync()
                If files Is Nothing OrElse files.Length = 0 Then Return Nothing

                Dim paths = ToLocalPaths(files)
                If paths.Count = 0 Then Return New ClipboardPathData With {.ClipboardWasReadable = True}

                Dim isCut = False
                Try
                    isCut = String.Equals(Await transfer.TryGetValueAsync(CutFormat), "1", StringComparison.Ordinal)
                Catch
                End Try

                Return New ClipboardPathData With {.Paths = paths, .IsCut = isCut, .ClipboardWasReadable = True}
            Catch
                Return Nothing
            Finally
                If ownsTransfer AndAlso transfer IsNot Nothing Then transfer.Dispose()
            End Try
        End Function

        Private Shared Function TryGetLocalPath(item As IStorageItem) As String
            Try
                Dim uri = item?.Path
                If uri Is Nothing OrElse Not uri.IsAbsoluteUri OrElse Not uri.IsFile Then Return Nothing
                Return uri.LocalPath
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function ParsePathText(text As String) As ClipboardPathData
            Dim result As New List(Of String)()
            Dim isCut = False
            If String.IsNullOrWhiteSpace(text) Then Return New ClipboardPathData()

            For Each rawLine In text.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                Dim line = rawLine.Trim()
                If line.Length = 0 OrElse line.StartsWith("#") Then Continue For
                If String.Equals(line, "copy", StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.Equals(line, "cut", StringComparison.OrdinalIgnoreCase) Then
                    isCut = True
                    Continue For
                End If

                Dim path = line
                If line.StartsWith("file://", StringComparison.OrdinalIgnoreCase) Then
                    Try
                        Dim uri = New Uri(line)
                        If Not uri.IsFile Then Continue For
                        path = uri.LocalPath
                    Catch
                        Continue For
                    End Try
                End If
                If IO.File.Exists(path) OrElse IO.Directory.Exists(path) Then result.Add(path)
            Next

            Return New ClipboardPathData With {
                .Paths = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                .IsCut = isCut
            }
        End Function
    End Class
End Namespace

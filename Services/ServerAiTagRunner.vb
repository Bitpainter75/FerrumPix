Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Analysiert Serverbilder bewusst nur auf ausdrücklichen Wunsch. Die Originale werden
    ''' nacheinander als vorhandene Temp-Kopien geladen; Begriffe landen ausschließlich im lokalen
    ''' Index der jeweiligen Serverquelle.</summary>
    Public NotInheritable Class ServerAiTagRunner
        Private Sub New()
        End Sub

        ''' <summary>Analysiert genau ein Galerieelement. Serverbilder werden dabei nur fuer die
        ''' Analyse als Temp-Original geholt; die Begriffe bleiben im jeweiligen lokalen Serverindex.</summary>
        Public Shared Async Function AnalyzeItemAsync(item As Models.ImageItem, token As CancellationToken) As Task(Of List(Of AiImageTag))
            If item Is Nothing OrElse Not ImageTaggingService.Available Then Return Nothing
            Dim localPath = Await item.EnsureLocalOriginalAsync().ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(localPath) Then Return Nothing
            Dim tags = Await Task.Run(Function() ImageTaggingService.AnalyzeFile(localPath, token), token).ConfigureAwait(False)
            If tags Is Nothing Then Return Nothing
            If item.IsImmichAsset Then
                Dim sourceVersion = ImmichIndexService.Instance.GetAssetUpdatedAt(ImmichService.ServerKey, item.ImmichAssetId)
                If String.IsNullOrEmpty(sourceVersion) Then
                    Dim detail = Await ImmichService.GetAssetDetailAsync(item.ImmichAssetId, token).ConfigureAwait(False)
                    sourceVersion = If(detail?.UpdatedAt, "")
                End If
                ImmichIndexService.Instance.ReplaceAiTags(ImmichService.ServerKey, item.ImmichAssetId, sourceVersion, tags)
            ElseIf item.IsNextcloudAsset Then
                NextcloudIndexService.Instance.ReplaceAiTags(NextcloudService.ServerKey, item.NextcloudFileId, item.NextcloudETag, tags)
            Else
                Return Nothing
            End If
            Return tags
        End Function

        Public Shared Async Function RunImmichAsync(progress As IProgress(Of ServerIndexProgress), token As CancellationToken) As Task(Of ServerIndexResult)
            Dim result As New ServerIndexResult()
            If Not ImmichService.IsConfigured OrElse Not ImageTaggingService.Available Then Return result
            ImageTaggingService.ClearAbandonedWorkCopies()
            Dim assets As New List(Of ImmichAsset)()
            Dim page = 1
            Do
                token.ThrowIfCancellationRequested()
                Dim batch = Await ImmichService.GetAssetsPageAsync(page, cancellationToken:=token).ConfigureAwait(False)
                If batch Is Nothing OrElse batch.Items Is Nothing OrElse batch.Items.Count = 0 Then Exit Do
                assets.AddRange(batch.Items.FindAll(Function(a) a IsNot Nothing AndAlso Not a.IsVideo))
                If batch.NextPage <= 0 Then Exit Do
                page = batch.NextPage
            Loop
            Return Await RunImmichAssetsAsync(assets, progress, token).ConfigureAwait(False)
        End Function

        Public Shared Async Function RunNextcloudAsync(progress As IProgress(Of ServerIndexProgress), token As CancellationToken) As Task(Of ServerIndexResult)
            Dim result As New ServerIndexResult()
            If Not NextcloudService.IsConfigured OrElse Not ImageTaggingService.Available Then Return result
            ImageTaggingService.ClearAbandonedWorkCopies()
            Dim photos As New List(Of NextcloudService.NextcloudPhoto)()
            Dim seen As New HashSet(Of Long)()
            For Each dayEntry In Await NextcloudService.GetDaysAsync(token).ConfigureAwait(False)
                token.ThrowIfCancellationRequested()
                For Each photo In Await NextcloudService.GetDayAsync(dayEntry.DayId, token).ConfigureAwait(False)
                    If photo IsNot Nothing AndAlso seen.Add(photo.FileId) Then photos.Add(photo)
                Next
            Next
            result.Total = photos.Count
            For i = 0 To photos.Count - 1
                If token.IsCancellationRequested Then result.Cancelled = True : Exit For
                Dim photo = photos(i)
                Dim localPath As String = Nothing
                Try
                    If Not NextcloudIndexService.Instance.AiTagsNeedRefresh(NextcloudService.ServerKey, photo.FileId.ToString(CultureInfo.InvariantCulture), photo.ETag) Then
                        result.Indexed += 1
                        progress?.Report(New ServerIndexProgress With {.Done = i + 1, .Total = result.Total})
                        Continue For
                    End If
                    localPath = Await NextcloudService.DownloadOriginalToWorkTempAsync(photo.FileId.ToString(CultureInfo.InvariantCulture), photo.DisplayName, token).ConfigureAwait(False)
                    If String.IsNullOrWhiteSpace(localPath) Then
                        result.Failed += 1
                    Else
                        Dim tags = Await Task.Run(Function() ImageTaggingService.AnalyzeFile(localPath, token), token).ConfigureAwait(False)
                        If tags Is Nothing Then
                            result.Failed += 1
                        Else
                            NextcloudIndexService.Instance.ReplaceAiTags(NextcloudService.ServerKey, photo.FileId.ToString(CultureInfo.InvariantCulture), photo.ETag, tags)
                            result.Indexed += 1
                        End If
                    End If
                Catch ex As OperationCanceledException
                    result.Cancelled = True : Exit For
                Catch ex As Exception
                    DiagnosticLogService.LogException("Nextcloud.KIStichwörter", ex) : result.Failed += 1
                Finally
                    DeleteWorkCopy(localPath)
                End Try
                progress?.Report(New ServerIndexProgress With {.Done = i + 1, .Total = result.Total})
            Next
            Return result
        End Function

        Private Shared Async Function RunImmichAssetsAsync(assets As List(Of ImmichAsset), progress As IProgress(Of ServerIndexProgress), token As CancellationToken) As Task(Of ServerIndexResult)
            Dim result As New ServerIndexResult With {.Total = assets.Count}
            For i = 0 To assets.Count - 1
                If token.IsCancellationRequested Then result.Cancelled = True : Exit For
                Dim asset = assets(i)
                Dim localPath As String = Nothing
                Try
                    If Not ImmichIndexService.Instance.AiTagsNeedRefresh(ImmichService.ServerKey, asset.Id, asset.UpdatedAt) Then
                        result.Indexed += 1
                        progress?.Report(New ServerIndexProgress With {.Done = i + 1, .Total = result.Total})
                        Continue For
                    End If
                    localPath = Await ImmichService.DownloadOriginalToWorkTempAsync(asset.Id, asset.FileName, token).ConfigureAwait(False)
                    If String.IsNullOrWhiteSpace(localPath) Then
                        result.Failed += 1
                    Else
                        Dim tags = Await Task.Run(Function() ImageTaggingService.AnalyzeFile(localPath, token), token).ConfigureAwait(False)
                        If tags Is Nothing Then
                            result.Failed += 1
                        Else
                            ImmichIndexService.Instance.ReplaceAiTags(ImmichService.ServerKey, asset.Id, asset.UpdatedAt, tags)
                            result.Indexed += 1
                        End If
                    End If
                Catch ex As OperationCanceledException
                    result.Cancelled = True : Exit For
                Catch ex As Exception
                    DiagnosticLogService.LogException("Immich.KIStichwörter", ex) : result.Failed += 1
                Finally
                    DeleteWorkCopy(localPath)
                End Try
                progress?.Report(New ServerIndexProgress With {.Done = i + 1, .Total = result.Total})
            Next
            Return result
        End Function

        Private Shared Sub DeleteWorkCopy(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            Try
                If IO.File.Exists(path) Then IO.File.Delete(path)
            Catch ex As Exception
                DiagnosticLogService.LogException("Server.KIStichwörterTempAufräumen", ex)
            End Try
        End Sub
    End Class
End Namespace

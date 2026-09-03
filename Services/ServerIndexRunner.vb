Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading

Namespace Services

    ''' <summary>Was ein Serverlauf ergeben hat.</summary>
    Public Class ServerIndexResult
        ''' <summary>Wie viele Aufnahmen ihre Einzelheiten frisch vom Server bekommen haben.</summary>
        Public Property Indexed As Integer
        ''' <summary>Wie viele übersprungen wurden, weil der gespeicherte Stand noch galt.</summary>
        Public Property Unchanged As Integer
        ''' <summary>Wie viele sich nicht abrufen ließen.</summary>
        Public Property Failed As Integer
        ''' <summary>Wie viele Aufnahmen der Server insgesamt genannt hat.</summary>
        Public Property Total As Integer
        Public Property Cancelled As Boolean
        ''' <summary>True, wenn der Server nicht erreichbar war oder gar nichts genannt hat. Dann ist
        ''' das Ergebnis KEINE Aussage über den Bestand - siehe <see cref="ServerIndexRunner"/>.</summary>
        Public Property ServerSilent As Boolean
        ''' <summary>Was der Dienst zuletzt gemeldet hat, wenn etwas schiefging.</summary>
        Public Property ErrorMessage As String = ""
    End Class

    Public Class ServerIndexProgress
        Public Property Done As Integer
        Public Property Total As Integer
    End Class

    ''' <summary>Holt die Einzelheiten ALLER Aufnahmen eines Servers in den lokalen Index - für
    ''' Immich wie für Nextcloud, über denselben Ablauf.
    '''
    ''' WOFÜR: Beide Indizes füllen sich sonst nur nebenbei, wenn eine Kachel sichtbar wird
    ''' (<c>ImageItem.RequestImmichDetailOnce</c> und das Gegenstück für Nextcloud). Alles, wo
    ''' niemand vorbeigescrollt ist, bleibt leer - und eine Suchbedingung auf Kamera, ISO, Blende
    ''' oder Brennweite greift dort nicht.
    '''
    ''' ER LÄUFT NUR AUF ANSAGE. Kein Start beim Programmstart, kein Zeitplan, kein Mitlaufen im
    ''' Hintergrund: er erzeugt bei einem großen Bestand zehntausende Anfragen gegen einen fremden
    ''' Server, und das ist etwas anderes als Plattenzugriffe auf eigene Ordner. Ausgelöst wird er
    ''' ausschließlich über den Knopf im jeweiligen Serverbereich der Einstellungen.
    '''
    ''' ER RÄUMT NICHTS AUF. Kein Katalogeintrag, keine Kachel, unter keinen Umständen - auch nicht
    ''' für Aufnahmen, die der Server nicht mehr nennt. Ein Server, der gerade nicht antwortet, eine
    ''' abgelaufene Anmeldung oder ein leeres Album sähen für einen aufräumenden Lauf aus wie ein
    ''' gelöschter Bestand, und weg wären Bewertungen, Etiketten und Stichwörter. Aufgeräumt wird
    ''' getrennt und auf Ansage (siehe die Aufräumknöpfe daneben) - dieselbe Trennung wie beim
    ''' Katalogindex über eigene Ordner.
    '''
    ''' SCHWEIGT DER SERVER, IST DAS ERGEBNIS KEINE AUSSAGE. Nennt er keine einzige Aufnahme, wird
    ''' das als <see cref="ServerIndexResult.ServerSilent"/> gemeldet statt als "0 gefunden, alles
    ''' erledigt". Der Unterschied zählt: das eine heißt "nichts zu tun", das andere "wir wissen
    ''' nichts".</summary>
    Public NotInheritable Class ServerIndexRunner

        Private Sub New()
        End Sub

        Private Shared _running As Integer

        ''' <summary>Läuft gerade einer? EIN Merker für beide Server: sie teilen sich die Leitung
        ''' und die Drosselung, und zwei gleichzeitige Läufe wären für den Anwender ohnehin nur
        ''' "es lädt".</summary>
        Public Shared ReadOnly Property IsRunning As Boolean
            Get
                Return Volatile.Read(_running) <> 0
            End Get
        End Property

        Public Shared Async Function RunImmichAsync(Optional progress As IProgress(Of ServerIndexProgress) = Nothing,
                                                    Optional cancellationToken As CancellationToken = Nothing) As Task(Of ServerIndexResult)
            Return Await RunAsync(AddressOf CollectImmichAssetsAsync, AddressOf FetchImmichDetailAsync,
                                  progress, cancellationToken).ConfigureAwait(False)
        End Function

        Public Shared Async Function RunNextcloudAsync(Optional progress As IProgress(Of ServerIndexProgress) = Nothing,
                                                       Optional cancellationToken As CancellationToken = Nothing) As Task(Of ServerIndexResult)
            Return Await RunAsync(AddressOf CollectNextcloudAssetsAsync, AddressOf FetchNextcloudDetailAsync,
                                  progress, cancellationToken).ConfigureAwait(False)
        End Function

        ''' <summary>Der gemeinsame Ablauf. Die beiden Server unterscheiden sich nur darin, WIE sie
        ''' ihre Aufnahmen aufzählen und WIE eine einzelne nachgeladen wird; alles andere - Abbruch,
        ''' Fortschritt, Drosselung und vor allem die Regel "nichts aufräumen" - steht hier einmal.
        ''' Zwei Fassungen davon liefen garantiert auseinander, und ausgerechnet bei der Regel, die
        ''' Daten kostet, wenn sie fehlt.</summary>
        Private Shared Async Function RunAsync(collect As Func(Of CancellationToken, Task(Of List(Of ServerAssetRef))),
                                               fetch As Func(Of ServerAssetRef, CancellationToken, Task(Of Boolean?)),
                                               progress As IProgress(Of ServerIndexProgress),
                                               cancellationToken As CancellationToken) As Task(Of ServerIndexResult)
            Dim result As New ServerIndexResult()
            If Interlocked.CompareExchange(_running, 1, 0) <> 0 Then
                result.ServerSilent = True
                result.ErrorMessage = LocalizationService.T("Es läuft bereits ein Serverdurchlauf.")
                Return result
            End If

            Try
                Dim assets As List(Of ServerAssetRef)
                Try
                    assets = Await collect(cancellationToken).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    result.Cancelled = True
                    Return result
                Catch ex As Exception
                    DiagnosticLogService.LogException("Serverindex.Sammeln", ex)
                    result.ServerSilent = True
                    result.ErrorMessage = ex.Message
                    Return result
                End Try

                If assets Is Nothing OrElse assets.Count = 0 Then
                    ' NICHT als "nichts zu tun" melden. Ein Server, der schweigt, sieht genauso aus
                    ' wie ein leerer Bestand - und der Unterschied entscheidet, ob ein Aufräumen
                    ' danach etwas löschen darf.
                    result.ServerSilent = True
                    Return result
                End If

                result.Total = assets.Count
                progress?.Report(New ServerIndexProgress With {.Done = 0, .Total = result.Total})

                Dim done = 0
                For Each asset In assets
                    If cancellationToken.IsCancellationRequested Then
                        result.Cancelled = True
                        Exit For
                    End If
                    Try
                        ' True = frisch geholt, False = der gespeicherte Stand galt noch,
                        ' Nothing = ging nicht.
                        Dim fetched = Await fetch(asset, cancellationToken).ConfigureAwait(False)
                        If Not fetched.HasValue Then
                            result.Failed += 1
                        ElseIf fetched.Value Then
                            result.Indexed += 1
                        Else
                            result.Unchanged += 1
                        End If
                    Catch ex As OperationCanceledException
                        result.Cancelled = True
                        Exit For
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Serverindex.Abruf", ex)
                        result.Failed += 1
                    End Try
                    done += 1
                    progress?.Report(New ServerIndexProgress With {.Done = done, .Total = result.Total})
                Next
                Return result
            Finally
                Volatile.Write(_running, 0)
            End Try
        End Function

        ''' <summary>Eine Aufnahme, so weit der Lauf sie kennen muss: ihre Kennung und den Wert, an
        ''' dem der gespeicherte Stand veraltet (Immichs <c>updatedAt</c>, Nextclouds Etag).</summary>
        Public Class ServerAssetRef
            Public Property Id As String = ""
            Public Property Version As String = ""
        End Class

        ' ── Immich ──────────────────────────────────────────────────────────────

        Private Shared Async Function CollectImmichAssetsAsync(cancellationToken As CancellationToken) As Task(Of List(Of ServerAssetRef))
            Dim result As New List(Of ServerAssetRef)()
            If Not ImmichService.IsConfigured Then Return result
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Dim page = 1
            ' Eine Obergrenze, damit ein Server, der immer dieselbe Seite liefert, den Lauf nicht
            ' ewig drehen laesst.
            Const maxPages As Integer = 2000
            While page <= maxPages
                cancellationToken.ThrowIfCancellationRequested()
                Dim seite = Await ImmichService.GetAssetsPageAsync(page, cancellationToken:=cancellationToken).ConfigureAwait(False)
                If seite Is Nothing OrElse seite.Items Is Nothing OrElse seite.Items.Count = 0 Then Exit While
                For Each asset In seite.Items
                    If asset Is Nothing OrElse String.IsNullOrEmpty(asset.Id) Then Continue For
                    If Not seen.Add(asset.Id) Then Continue For
                    result.Add(New ServerAssetRef With {.Id = asset.Id, .Version = If(asset.UpdatedAt, "")})
                Next
                If seite.NextPage <= 0 Then Exit While
                page = seite.NextPage
            End While
            Return result
        End Function

        Private Shared Async Function FetchImmichDetailAsync(asset As ServerAssetRef,
                                                             cancellationToken As CancellationToken) As Task(Of Boolean?)
            Dim key = ImmichService.ServerKey
            If ImmichIndexService.Instance.TryGet(key, asset.Id, asset.Version) IsNot Nothing Then Return False
            Dim detail = Await ImmichService.GetAssetDetailCachedAsync(asset.Id, asset.Version, cancellationToken).ConfigureAwait(False)
            If detail Is Nothing Then Return Nothing
            Return True
        End Function

        ' ── Nextcloud ───────────────────────────────────────────────────────────

        Private Shared Async Function CollectNextcloudAssetsAsync(cancellationToken As CancellationToken) As Task(Of List(Of ServerAssetRef))
            Dim result As New List(Of ServerAssetRef)()
            If Not NextcloudService.IsConfigured Then Return result
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Dim days = Await NextcloudService.GetDaysAsync(cancellationToken).ConfigureAwait(False)
            For Each tag In If(days, New List(Of NextcloudService.NextcloudDay)())
                cancellationToken.ThrowIfCancellationRequested()
                Dim photos = Await NextcloudService.GetDayAsync(tag.DayId, cancellationToken).ConfigureAwait(False)
                For Each photo In If(photos, New List(Of NextcloudService.NextcloudPhoto)())
                    If photo Is Nothing Then Continue For
                    Dim id = photo.FileId.ToString(Globalization.CultureInfo.InvariantCulture)
                    If Not seen.Add(id) Then Continue For
                    result.Add(New ServerAssetRef With {.Id = id, .Version = If(photo.ETag, "")})
                Next
            Next
            Return result
        End Function

        Private Shared Async Function FetchNextcloudDetailAsync(asset As ServerAssetRef,
                                                                cancellationToken As CancellationToken) As Task(Of Boolean?)
            ' OHNE ETAG GAR NICHT: der Eintrag liesse sich nie wieder als veraltet erkennen, und der
            ' Index legt ihn deshalb ohnehin nicht ab (siehe NextcloudIndexService).
            If String.IsNullOrEmpty(asset.Version) Then Return Nothing
            Dim key = NextcloudService.ServerKey
            If NextcloudIndexService.Instance.TryGet(key, asset.Id, asset.Version) IsNot Nothing Then Return False
            Dim info = Await NextcloudService.GetInfoCachedAsync(asset.Id, asset.Version, cancellationToken).ConfigureAwait(False)
            If info Is Nothing Then Return Nothing
            Return True
        End Function

    End Class

End Namespace

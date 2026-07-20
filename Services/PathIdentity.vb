Imports System
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>
    ''' Identitaet eines LOKALEN DATEIPFADS - eine Stelle, an der entschieden wird, ob zwei Pfade
    ''' dieselbe Datei meinen.
    '''
    ''' Der Punkt: unter Windows sind Pfade case-insensitiv, unter Linux nicht. `RAW.jpg` und
    ''' `raw.jpg` koennen im selben Ordner ZWEI verschiedene Dateien sein. Die App hat Pfade lange
    ''' ueberall mit `OrdinalIgnoreCase` verglichen und in `ToUpperInvariant`-Schluessel gesteckt;
    ''' auf Linux fasste das verschiedene Dateien zu einer zusammen - mit der Folge, dass
    ''' Thumbnail-, EXIF- und Transparenz-Caches Daten vermischen konnten und eine von zwei Dateien
    ''' aus Auswahl, Zwischenablage oder Drag-Nutzlast verschwand.
    '''
    ''' WICHTIG - das gilt NUR fuer echte lokale Dateipfade. Weiterhin case-insensitiv verglichen
    ''' werden (und das ist dort richtig):
    '''   * Dateiendungen und Formatnamen (".JPG" ist dasselbe Format wie ".jpg"),
    '''   * Tags, Suchbegriffe, Kameramodelle, Labels,
    '''   * technische Kennungen und Immich-Pseudopfade (die kommen vom Server, nicht vom
    '''     Dateisystem, und sind dort eindeutig).
    ''' Deshalb ist dieser Helfer bewusst NICHT als globales Suchen-und-Ersetzen gedacht.
    '''
    ''' macOS: HFS+/APFS sind standardmaessig case-INsensitiv, koennen aber case-sensitiv
    ''' formatiert sein. Wir behandeln macOS wie Unix (case-sensitiv). Das ist die sichere Richtung:
    ''' zwei Pfade faelschlich getrennt zu halten kostet hoechstens einen doppelten Cache-Eintrag,
    ''' sie faelschlich zu verschmelzen vermischt dagegen die Daten zweier Dateien.
    ''' </summary>
    Public NotInheritable Class PathIdentity

        Private Sub New()
        End Sub

        ''' <summary>True, wenn das Dateisystem der Plattform Gross-/Kleinschreibung ignoriert.</summary>
        Public Shared ReadOnly Property IgnoresCase As Boolean
            Get
                Return OperatingSystem.IsWindows()
            End Get
        End Property

        ''' <summary>Vergleicher fuer lokale Dateipfade - fuer Dictionary, HashSet, Distinct, Contains.</summary>
        Public Shared ReadOnly Property Comparer As StringComparer
            Get
                Return If(IgnoresCase, StringComparer.OrdinalIgnoreCase, StringComparer.Ordinal)
            End Get
        End Property

        ''' <summary>Vergleichsart fuer String.Equals/StartsWith auf lokalen Dateipfaden.</summary>
        Public Shared ReadOnly Property Comparison As StringComparison
            Get
                Return If(IgnoresCase, StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)
            End Get
        End Property

        ''' <summary>Kanonische Form eines Pfads fuer Schluessel und Hashes: absoluter Pfad, ohne
        ''' abschliessenden Trenner, und nur unter Windows in einheitlicher Schreibweise.
        ''' Beide Trennzeichen werden abgeschnitten - unter Windows ist AltDirectorySeparatorChar der
        ''' Schraegstrich, und ein von Hand eingetragener Pfad kann auf jedem von beiden enden.</summary>
        Public Shared Function Normalize(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Dim result As String
            Try
                result = IO.Path.GetFullPath(path)
            Catch
                result = path.Trim()
            End Try
            result = result.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            Return If(IgnoresCase, result.ToUpperInvariant(), result)
        End Function

        ''' <summary>Meinen die beiden Pfade dieselbe Datei?</summary>
        Public Shared Function AreSame(a As String, b As String) As Boolean
            Return String.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal)
        End Function

        ''' <summary>Liegt <paramref name="path"/> in <paramref name="folder"/> oder IST es der
        ''' Ordner? Vergleicht auf Trennzeichen-Grenze, damit "/a/bc" nicht als Kind von "/a/b"
        ''' gilt.</summary>
        Public Shared Function IsAncestorOrSelf(folder As String, candidate As String) As Boolean
            Dim f = Normalize(folder)
            Dim p = Normalize(candidate)
            If f.Length = 0 OrElse p.Length = 0 Then Return False
            If String.Equals(f, p, StringComparison.Ordinal) Then Return True
            Return p.StartsWith(f + IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
        End Function

        ''' <summary>Dubletten aus einer Pfadliste entfernen, Reihenfolge bleibt erhalten.</summary>
        Public Shared Function Distinct(paths As IEnumerable(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            For Each p In If(paths, Array.Empty(Of String)())
                If String.IsNullOrWhiteSpace(p) Then Continue For
                If seen.Add(Normalize(p)) Then result.Add(p)
            Next
            Return result
        End Function

    End Class

End Namespace

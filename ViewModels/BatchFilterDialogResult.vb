Imports System.IO

Namespace ViewModels

    ''' <summary>Auswahl aus dem Dialog "Filter anwenden" (Galerie, Stapelverarbeitung).</summary>
    Public Class BatchFilterDialogResult

        Public Const SourceFilter As String = "Filter"
        Public Const SourceLightroom As String = "Lightroom"
        Public Const SourceLut As String = "Lut"

        ''' <summary>Woher der Look kommt: eingebauter Filter, Lightroom-Preset (.xmp) oder LUT (.cube).</summary>
        Public Property SourceKind As String = SourceFilter

        ''' <summary>Name des Filters bzw. des Presets - landet bei "Neue Dateien" auch im Dateinamen.</summary>
        Public Property DisplayName As String = ""

        ''' <summary>Pfad der .xmp-/.cube-Datei; leer bei eingebauten Filtern.</summary>
        Public Property PresetPath As String = ""

        ''' <summary>Wirkung in Prozent. Nur für Filter und LUT - ein Lightroom-Preset ist eine Sammlung
        ''' einzelner Regler und kennt keinen gemeinsamen Mischregler.</summary>
        Public Property Strength As Integer = 100

        ''' <summary>True: Originale werden überschrieben (Format, Ziel und Namenszusatz entfallen).</summary>
        Public Property Overwrite As Boolean = False

        Public Property AppendNameToFileName As Boolean = True
        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90

        ''' <summary>Zielort für neue Dateien: "Local" (Ordner) oder "Immich" (Upload als neues Asset).</summary>
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""

        ''' <summary>Welche Katalog-Metadaten die Kopie vom Original übernimmt (Zeile „Übernehmen").
        ''' Beim Überschreiben ohne Bedeutung - die Datei behält ihren Katalog-Eintrag.</summary>
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True

        Public ReadOnly Property MetaCopy As CatalogMetaCopyOptions
            Get
                Return New CatalogMetaCopyOptions With {
                    .CopyRating = CopyRating,
                    .CopyFavorite = CopyFavorite,
                    .CopyColorLabel = CopyColorLabel,
                    .CopyKeywords = CopyKeywords
                }
            End Get
        End Property

        Public ReadOnly Property Extension As String
            Get
                Select Case If(Format, "").Trim().ToUpperInvariant()
                    Case "PNG"
                        Return ".png"
                    Case "WEBP"
                        Return ".webp"
                    Case Else
                        Return ".jpg"
                End Select
            End Get
        End Property

        ''' <summary>Der Namenszusatz für neue Dateien ("foto.jpg" -> "foto_Vintage.jpg"). Leer, wenn er
        ''' nicht gewünscht ist. Zeichen, die in Dateinamen nicht erlaubt sind, werden ersetzt - der
        ''' eingebaute Filter "S/W" würde sonst einen Unterordner aufmachen.</summary>
        Public ReadOnly Property FileNameSuffix As String
            Get
                If Overwrite OrElse Not AppendNameToFileName Then Return ""
                Dim name = If(DisplayName, "").Trim()
                If name.Length = 0 Then Return ""
                For Each c In Path.GetInvalidFileNameChars()
                    name = name.Replace(c, "-"c)
                Next
                name = name.Replace(" "c, "-"c)
                Return "_" & name
            End Get
        End Property

    End Class

End Namespace

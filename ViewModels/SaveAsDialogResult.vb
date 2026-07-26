Namespace ViewModels

    Public Class SaveAsDialogResult
        ''' <summary>Datei-Metadaten (EXIF/XMP) der Quelle in die Zieldatei uebernehmen -
        ''' der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs.</summary>
        Public Property PreserveMetadata As Boolean = True
        Public Property BaseName As String
        Public Property Format As String
        Public Property JpgQuality As Integer
        ''' Zielort: "Local" (Ordner) oder "Immich" (Upload als neues Asset).
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""

        ''' Einzeloptionen: welche Katalog-Metadaten auf die neue
        ''' Datei übernommen werden. Standard alles an = bisheriges Verhalten.
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True

        ''' <summary>Dateinamen-Muster für Stapel-Ziele (leer = Originalname); nur von den
        ''' Stapel-Dialogen befüllt, der Einzel-Speichern-Dialog hat sein eigenes Namensfeld.</summary>
        Public Property NamePattern As String = ""

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
                    Case "FPX"
                        Return ".fpx"
                    Case "PDF"
                        Return ".pdf"
                    Case Else
                        Return ".jpg"
                End Select
            End Get
        End Property

        ''' <summary>True, wenn ein druckfertiges PDF geschrieben wird (eine Seite, Seitenlayout aus
        ''' den zuletzt im Druckdialog gewählten Optionen). Wie FPX kein Immich-Ziel.</summary>
        Public ReadOnly Property IsPdf As Boolean
            Get
                Return String.Equals(Format, "PDF", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>True, wenn als nicht-destruktives FerrumPix-Projekt (.fpx-Bündel) gespeichert wird.</summary>
        Public ReadOnly Property IsFpx As Boolean
            Get
                Return String.Equals(Format, "FPX", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property FileName As String
            Get
                Return BaseName & Extension
            End Get
        End Property
    End Class

End Namespace

Imports System.Collections.Generic
Imports FerrumPix.Services

Namespace ViewModels

    Public Enum AppDialogKind
        Message
        Input
        Rename
        SaveAs
        FileConflict
        BatchRename
        Search
        BatchConvert
        BatchResize
        BatchFilter
        WatermarkPreset
    End Enum

    Public Enum FileConflictChoice
        Cancel
        Skip
        SkipAll
        Overwrite
        OverwriteAll
        Rename
    End Enum

    Public Class FileConflictDialogResult
        Public Property Choice As FileConflictChoice
        Public Property NewName As String
    End Class

    Public Class BatchRenameMapping
        Public Property SourcePath As String
        Public Property TargetPath As String
    End Class

    Public Class BatchRenameResult
        Public Property Mappings As List(Of BatchRenameMapping)
    End Class

    Public Class SearchDialogResult
        Public Property Name As String
        ''' "Local" (Dateisystem) oder "Immich" (Server-Suche).
        Public Property Source As String = "Local"
        Public Property TextQuery As String
        Public Property RootFolder As String
        Public Property IncludeSubfolders As Boolean
        Public Property FavoriteMode As String
        Public Property RatingMin As Integer
        Public Property Ratings As List(Of Integer)
        Public Property Conditions As List(Of SearchCondition)
        Public Property ConditionCombinator As String
    End Class

    ''' <summary>Welche Katalog-Metadaten eine neu geschriebene Datei vom Original erbt - die Zeile
    ''' „Übernehmen" gibt es in mehreren Dialogen, deshalb tragen die Ergebnisse sie einheitlich.</summary>
    Public Class CatalogMetaCopyOptions
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True
    End Class

    Public Class BatchResizeResult
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property ScalePercent As Integer
        Public Property LockAspect As Boolean
        Public Property Interpolation As ResizeInterpolationMode

        ''' <summary>True: Originale werden überschrieben (bisheriges Verhalten; Format, Ziel und
        ''' Namenszusatz entfallen). False: neue Dateien mit Formatauswahl wie beim Filter-Dialog.</summary>
        Public Property Overwrite As Boolean = True

        Public Property AppendSizeToFileName As Boolean = True
        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90

        ''' <summary>Zielort für neue Dateien: "Local" (Ordner) oder "Immich" (Upload als neues Asset).</summary>
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""

        ''' <summary>Welche Katalog-Metadaten die Kopie vom Original übernimmt (Zeile „Übernehmen" im
        ''' Dialog). Beim Überschreiben ohne Bedeutung - die Datei behält ihren Katalog-Eintrag.</summary>
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

        ''' <summary>Der Namenszusatz für neue Dateien ("foto.jpg" -> "foto_1920x1080.jpg" bzw.
        ''' "foto_50.jpg" bei prozentualer Skalierung). Leer, wenn er nicht gewünscht ist.</summary>
        Public ReadOnly Property FileNameSuffix As String
            Get
                If Overwrite OrElse Not AppendSizeToFileName Then Return ""
                If ScalePercent > 0 Then Return $"_{ScalePercent}"
                If Width > 0 AndAlso Height > 0 Then Return $"_{Width}x{Height}"
                If Width > 0 Then Return $"_{Width}px"
                If Height > 0 Then Return $"_{Height}px"
                Return ""
            End Get
        End Property
    End Class

    Public Class WatermarkPresetDialogResult
        Public Property Preset As WatermarkPresetSettings
        Public Property Overwrite As Boolean = True
        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""
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
    End Class

End Namespace

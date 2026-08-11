Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Was eine Zeile der Ordnerliste ist.</summary>
    Public Enum CatalogFolderRowKind
        ''' <summary>Ein ueberwachter Ordner. Seine Zahlen sind die von ihm SELBST und allem
        ''' darunter zusammen.</summary>
        WatchedRoot
        ''' <summary>Die Ueberschrift ueber allem, was unter keinem ueberwachten Ordner liegt.</summary>
        OtherHeader
        ''' <summary>Ein einzelner Ordner, zu dem Daten abgelegt sind.</summary>
        Folder
    End Enum

    ''' <summary>
    ''' Eine Zeile im Ordnerbereich der Einstellungen.
    '''
    ''' Die Liste fasst zwei Dinge zusammen, die vorher getrennt standen: die UEBERWACHTEN Ordner
    ''' (Wurzeln, rekursiv) und die Ordner, zu denen FerrumPix Daten abgelegt hat (je Ordner einer,
    ''' nicht rekursiv). Zusammengelegt, weil beides derselbe Gegenstand ist - ein Ordner - und die
    ''' Verwaltung sonst an zwei Stellen steht.
    '''
    ''' <para>GRUPPIERT und nicht flach: ein ueberwachter Ordner mit tausend Unterordnern erzeugt
    ''' tausend Cache-Zeilen. Flach nebeneinander waere die Wurzel darin nicht wiederzufinden, und
    ''' die Liste bestuende zu 99 Prozent aus Zeilen, die niemand einzeln braucht. Die Wurzel traegt
    ''' deshalb die Summe, und wer es genau wissen will, klappt auf.</para>
    ''' </summary>
    Public Class CatalogFolderRow
        Inherits ViewModelBase

        Private _isExpanded As Boolean

        ''' <summary>Wo die Kommandos dieser Zeile liegen.
        '''
        ''' WARUM EIN VERWEIS AUF DER ZEILE und nicht "RelativeSource AncestorType=UserControl":
        ''' Ein Flyout ist ein EIGENES POPUP und haengt nicht im visuellen Baum des Steuerelements,
        ''' aus dem es aufgeht. Der Weg ueber den Vorfahren findet dort nichts, das Kommando bleibt
        ''' leer, und Avalonia faerbt den Menuepunkt wortlos grau - genau so waren die Eintraege der
        ''' Ordnerzeilen nicht anklickbar (Nutzerbefund). Ueber den Datenkontext der Zeile kommt man
        ''' auch aus einem Popup heran.
        '''
        ''' Ein VERWEIS je Zeile und nicht sieben eigene Kommandos: bei einem aufgeklappten Bestand
        ''' mit tausend Ordnern waeren das siebentausend Objekte fuer nichts.</summary>
        Public Property Actions As SettingsViewModel

        Public Property Kind As CatalogFolderRowKind

        ''' <summary>Der Pfad. Bei <see cref="CatalogFolderRowKind.OtherHeader"/> leer.</summary>
        Public Property Path As String = ""

        ''' <summary>Wie tief eingerueckt. 0 fuer Wurzeln und Ueberschriften, 1 fuer alles darunter.</summary>
        Public Property Depth As Integer

        Public Property ThumbnailCount As Integer
        Public Property SizeBytes As Long
        Public Property CatalogCount As Integer

        ''' <summary>Wie viele Ordner unter dieser Wurzel Daten haben. Nur bei Wurzel und
        ''' Ueberschrift gesetzt.</summary>
        Public Property ChildCount As Integer

        ''' <summary>Liegt der Ordner noch auf der Platte? Ein ausgehaengtes Netzlaufwerk oder ein
        ''' geloeschter Ordner steht sonst da, als waere alles in Ordnung.</summary>
        Public Property Exists As Boolean = True

        ''' <summary>Die Ordner, die diese Zeile zusammenfasst - fuer das Aufraeumen ueber die ganze
        ''' Gruppe. Bei einer einzelnen Ordnerzeile steht sie selbst darin.</summary>
        Public Property Members As New List(Of ThumbnailCacheFolderInfo)()

        ''' <summary>Aufgeklappt? Nur Wurzeln und die Ueberschrift koennen das.</summary>
        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isExpanded, value)
            End Set
        End Property

        ''' <summary>Wurzel oder Ueberschrift - alles, was andere Zeilen zusammenfasst. Privat: nur
        ''' <see cref="CanExpand"/> fragt danach, und die Oberflaeche haengt an dem.</summary>
        Private ReadOnly Property IsGroup As Boolean
            Get
                Return Kind <> CatalogFolderRowKind.Folder
            End Get
        End Property

        Public ReadOnly Property IsWatched As Boolean
            Get
                Return Kind = CatalogFolderRowKind.WatchedRoot
            End Get
        End Property

        ''' <summary>Kann diese Zeile ueberwacht werden? Nur ein einzelner Ordner, der es noch nicht
        ''' ist - die Ueberschrift ist kein Ordner, und eine Wurzel ist es schon.</summary>
        Public ReadOnly Property CanWatch As Boolean
            Get
                Return Kind = CatalogFolderRowKind.Folder AndAlso Path.Length > 0
            End Get
        End Property

        ''' <summary>Klappt sie auf und zu? Eine Gruppe ohne Kinder nicht - ein Pfeil, hinter dem
        ''' nichts steckt, laesst zweimal klicken und nichts geschehen.</summary>
        Public ReadOnly Property CanExpand As Boolean
            Get
                Return IsGroup AndAlso ChildCount > 0
            End Get
        End Property

        ''' <summary>Was in der ersten Zeile steht.</summary>
        Public ReadOnly Property Title As String
            Get
                If Kind = CatalogFolderRowKind.OtherHeader Then
                    Return String.Format(LocalizationService.T("Weitere Ordner ({0})"), ChildCount)
                End If
                Return Path
            End Get
        End Property

        ''' <summary>Die Zahlen darunter. Bei einer Wurzel die Summe ueber alles darunter.</summary>
        Public ReadOnly Property DetailText As String
            Get
                If Kind = CatalogFolderRowKind.OtherHeader Then
                    Return String.Format(LocalizationService.T("{0} Vorschaubilder · {1} · {2} Katalogeinträge"),
                                         ThumbnailCount.ToString("N0"), FormatBytes(SizeBytes),
                                         CatalogCount.ToString("N0"))
                End If

                Dim text = String.Format(LocalizationService.T("{0} Vorschaubilder · {1} · {2} Katalogeinträge"),
                                         ThumbnailCount.ToString("N0"), FormatBytes(SizeBytes),
                                         CatalogCount.ToString("N0"))
                If Kind = CatalogFolderRowKind.WatchedRoot AndAlso ChildCount > 0 Then
                    text &= " · " & String.Format(LocalizationService.T("{0} Ordner"), ChildCount.ToString("N0"))
                End If
                If Not Exists Then text &= " · " & LocalizationService.T("Ordner fehlt")
                Return text
            End Get
        End Property

        ''' <summary>Gibt es hier ueberhaupt etwas aufzuraeumen? Ein ueberwachter Ordner, zu dem noch
        ''' nichts abgelegt ist, steht trotzdem in der Liste - er ist ja eingetragen -, aber sein
        ''' Aufraeum-Knopf haette nichts zu tun.</summary>
        Public ReadOnly Property HasAnything As Boolean
            Get
                Return ThumbnailCount > 0 OrElse CatalogCount > 0
            End Get
        End Property

        Private Shared Function FormatBytes(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes:N0} B"
            Dim kb = bytes / 1024.0
            If kb < 1024 Then Return $"{kb:N1} KB"
            Dim mb = kb / 1024.0
            If mb < 1024 Then Return $"{mb:N1} MB"
            Return $"{mb / 1024.0:N1} GB"
        End Function

    End Class

End Namespace

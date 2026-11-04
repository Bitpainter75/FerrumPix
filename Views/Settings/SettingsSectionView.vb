Imports Avalonia.Controls
Imports Avalonia.LogicalTree

Namespace Views.Settings

    ''' <summary>Gemeinsame Basis der Abschnitte der Einstellungsseite.
    '''
    ''' <para>WARUM EIGENE ANSICHTEN: die Seite war eine XAML von ueber viertausend Zeilen. Sie
    ''' entstand beim ersten Oeffnen auf einmal (gemessen rund 300 ms fuer das Erzeugen und 600 ms
    ''' fuer Stile, Vorlagen und das erste Vermessen) und lag danach ganz im Baum, auch wenn nur
    ''' ein Abschnitt zu sehen war. Jetzt baut <see cref="SettingsView"/> den ersten Abschnitt
    ''' sofort und die uebrigen danach von oben nach unten, je Abschnitt ein Schritt im Leerlauf.</para>
    '''
    ''' <para>DER RAHMEN BLEIBT IN DER SEITE: der aeussere <c>Border</c> mit Namen, Klasse
    ''' <c>section</c> und gebundener Sichtbarkeit steht weiter in SettingsView.axaml. Navigation,
    ''' Suche und das Rollen zu einem Abschnitt finden ihn dort ueber seinen Namen, auch bevor sein
    ''' Inhalt da ist. Hier steht nur dieser Inhalt.</para>
    '''
    ''' <para>DIE HANDLER BLEIBEN EBENFALLS IN DER SEITE. Jede Abschnittsdatei nennt nur, was ihre
    ''' XAML braucht, und reicht es an <see cref="Owner"/> weiter. Die Dateiwaehler haengen am
    ''' TopLevel, und eine Logik an zwei Stellen liefe auseinander.</para></summary>
    Public MustInherit Class SettingsSectionView
        Inherits UserControl

        ''' <summary>Die Einstellungsseite, in der dieser Abschnitt steht. Nothing, solange er nicht
        ''' eingehaengt ist; die Handler laufen erst danach.</summary>
        Protected ReadOnly Property Owner As SettingsView
            Get
                Return Me.FindLogicalAncestorOfType(Of SettingsView)()
            End Get
        End Property
    End Class

End Namespace

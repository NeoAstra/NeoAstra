# Embedded view sample

A main page with a second, completely isolated web view placed over a rectangle of it, as a browser pane beside an application.

```sh
dotnet run --project samples/NeoAstra.Sample.EmbeddedView
```

- The isolated view has an environment and a data folder of its own, and no bridge.
- The page reports the rectangle of its placeholder in CSS pixels, and `NeoAstra.PlaceOver` follows it through resize and zoom.
- **Hide or show** keeps the page. **Focus the view** gives it the keyboard.
- With the keyboard in the view, `Ctrl+Shift+K` (`Cmd+Shift+K` on macOS) reaches the application and not the page.
- **Move to the other window** keeps a typed text, without a reload.
- A click in the isolated page reaches the host through the binding of an isolated script world, which the page cannot use.

A menu or dialog of the main page that overlaps the view is drawn behind it, since the view is a native view above the page.

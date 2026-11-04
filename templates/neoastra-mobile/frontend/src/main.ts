import "./style.css"; import { greeting } from "#neoastra";
const app = document.querySelector<HTMLElement>("#app")!; app.innerHTML = "<h1>NeoAstra</h1><button type='button'>Say hello</button><output aria-live='polite'></output>"; app.querySelector("button")!.addEventListener("click", async () => { app.querySelector("output")!.textContent = (await greeting.hello({ name: "phone" })).message; });
// Vite runs this module again when it changes, instead of reloading the page: it renders #app anew each time.
if (import.meta.hot) import.meta.hot.accept();

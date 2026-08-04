import "./style.css"; import { greeting } from "#neoastra";
const app = document.querySelector<HTMLElement>("#app")!; app.innerHTML = "<h1>NeoAstra</h1><button type='button'>Say hello</button><output aria-live='polite'></output>"; app.querySelector("button")!.addEventListener("click", async () => { app.querySelector("output")!.textContent = (await greeting.hello({ name: "desktop" })).message; });

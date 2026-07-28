import { invoke } from "./neoastra-client/rpc.js";

const form = document.querySelector("#greeting-form");
const name = document.querySelector("#name");
const result = document.querySelector("#result");

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  result.textContent = "Calling C#…";
  try {
    const response = await invoke("greeting.hello", { name: name.value });
    result.textContent = response.message;
  } catch (error) {
    result.textContent = error instanceof Error ? error.message : "The greeting failed.";
  }
});

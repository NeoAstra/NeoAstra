import { greeting } from "./generated/neoastra.js";

const form = document.querySelector("#greeting-form");
const name = document.querySelector("#name");
const result = document.querySelector("#result");

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  result.textContent = "Calling C#…";
  try {
    const response = await greeting.hello({ name: name.value });
    result.textContent = response.message;
  } catch (error) {
    console.error("Greeting RPC failed", error);
    result.textContent = error instanceof Error ? error.message : "The greeting failed.";
  }
});

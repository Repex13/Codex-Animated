(() => {
  "use strict";

  const artwork = document.getElementById("artwork");
  const skip = document.getElementById("skip");
  const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
  let finished = false;
  let timeout;

  function finish(result) {
    if (finished) return;
    finished = true;
    window.clearTimeout(timeout);
    motion.removeEventListener("change", motionChanged);
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(result);
    }
  }

  function motionChanged(event) {
    if (event.matches) finish("complete");
  }

  skip.addEventListener("click", () => finish("complete"));
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      finish("complete");
    }
  });
  motion.addEventListener("change", motionChanged);

  if (motion.matches) {
    finish("complete");
    return;
  }

  artwork.decode().then(() => {
    if (finished) return;
    document.body.classList.add("playing");
    // Complete on a clock so disabling CSS animations cannot delay opening Codex.
    const seconds = parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--playback"));
    timeout = window.setTimeout(() => finish("complete"), seconds * 1000);
  }, () => finish("assetError"));
})();

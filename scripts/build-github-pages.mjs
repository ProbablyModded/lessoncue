import { cp, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputArgument = process.argv[2];

if (!outputArgument) {
  console.error("Usage: node scripts/build-github-pages.mjs OUTPUT_DIRECTORY");
  process.exit(1);
}

const outputDirectory = path.resolve(outputArgument);
const filesystemRoot = path.parse(repositoryRoot).root;
const isWithin = (candidate, parent) => candidate === parent || candidate.startsWith(`${parent}${path.sep}`);
if (outputDirectory === filesystemRoot || isWithin(outputDirectory, repositoryRoot) || isWithin(repositoryRoot, outputDirectory)) {
  throw new Error("Refusing to replace the repository or filesystem root.");
}

const escapeHtml = (value) => value
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;");

const inlineMarkdown = (value) => escapeHtml(value)
  .replace(/`([^`]+)`/g, "<code>$1</code>")
  .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
  .replace(/\*([^*]+)\*/g, "<em>$1</em>");

const isNumberHeading = (line) => /^\d+\.\s+/.test(line);
const isLetterHeading = (line) => /^[a-f]\.\s+/.test(line);
const isHeading = (line) => isNumberHeading(line) || isLetterHeading(line);

function renderPolicy(markdown) {
  const lines = markdown.replaceAll("\r\n", "\n").split("\n");
  const output = [];
  let index = 0;

  while (index < lines.length) {
    const line = lines[index].trim();
    if (!line) {
      index += 1;
      continue;
    }

    if (index === 0) {
      output.push(`<h1>${inlineMarkdown(line)}</h1>`);
      index += 1;
      continue;
    }

    if (/^Last updated:/i.test(line)) {
      output.push(`<p class="policy-meta">${inlineMarkdown(line)}</p>`);
      index += 1;
      continue;
    }

    if (isNumberHeading(line)) {
      output.push(`<h2>${inlineMarkdown(line)}</h2>`);
      index += 1;
      continue;
    }

    if (isLetterHeading(line)) {
      output.push(`<h3>${inlineMarkdown(line)}</h3>`);
      index += 1;
      continue;
    }

    // The source policy predates Markdown list markers. Its list blocks are
    // introduced by a colon, followed by plain lines until the next sentence
    // or numbered/lettered section. Render those blocks as real lists while
    // keeping privacy.md itself as the canonical text.
    if (line.endsWith(":")) {
      output.push(`<p>${inlineMarkdown(line)}</p>`);
      const items = [];
      let cursor = index + 1;
      while (cursor < lines.length) {
        const candidate = lines[cursor].trim();
        if (!candidate || isHeading(candidate)) break;
        if (items.length > 0 && /\.$/.test(candidate)) break;
        items.push(candidate);
        cursor += 1;
      }
      if (items.length > 0) {
        output.push(`<ul>${items.map((item) => `<li>${inlineMarkdown(item)}</li>`).join("")}</ul>`);
        index = cursor;
        continue;
      }
    }

    output.push(`<p>${inlineMarkdown(line)}</p>`);
    index += 1;
  }

  return output.join("\n");
}

const markdown = await readFile(path.join(repositoryRoot, "privacy.md"), "utf8");
await rm(outputDirectory, { recursive: true, force: true });
await cp(path.join(repositoryRoot, "github-pages"), outputDirectory, { recursive: true });
await writeFile(path.join(outputDirectory, "privacy.md"), markdown, "utf8");

const policyHtml = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta name="description" content="Privacy Policy for LessonCue.">
  <title>Privacy Policy · LessonCue</title>
  <link rel="icon" href="assets/lessoncue-icon.svg" type="image/svg+xml">
  <link rel="stylesheet" href="styles.css">
  <link rel="stylesheet" href="privacy.css">
</head>
<body>
  <nav><a class="brand" href="./"><img src="assets/lessoncue-icon.svg" alt=""> LessonCue</a><div><a href="./">Overview</a><a href="install.html">Install</a><a href="implementation.html">Build</a><a href="roadmap.html">Roadmap</a><a class="repo" href="https://github.com/ProbablyModded/lessoncue">GitHub ↗</a></div></nav>
  <main class="page policy-page">
    <p class="eyebrow">LEGAL</p>
    <section class="policy-content">
${renderPolicy(markdown).split("\n").map((line) => `      ${line}`).join("\n")}
    </section>
    <p><a class="secondary" href="privacy.md">View the source Markdown ↗</a></p>
  </main>
  <footer><a class="brand" href="./"><img src="assets/lessoncue-icon.svg" alt=""> LessonCue</a><p>Self-hosted media scheduling for schools, churches, and learning organizations.</p><a href="https://github.com/ProbablyModded/lessoncue">Source and releases ↗</a></footer>
</body>
</html>
`;
await writeFile(path.join(outputDirectory, "privacy.html"), policyHtml, "utf8");

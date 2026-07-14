// Blog seed for MongoDB — runs as admin in /docker-entrypoint-initdb.d/.
// Target appdb explicitly (default init db is not guaranteed).
var appdb = db.getSiblingDB("appdb");

appdb.getCollection("authors").insertMany([
  { _id: 1, name: "Alice Chen",   email: "alice@example.com" },
  { _id: 2, name: "Bob Martinez", email: "bob@example.com"   },
  { _id: 3, name: "Carol Davies", email: "carol@example.com" }
]);

appdb.getCollection("posts").insertMany([
  { _id: 1, author_id: 1,
    title: "Getting Started with Docker",
    body: "Docker simplifies container workflows.",
    published_at: "2026-01-10",
    tags: ["docker", "devops"],
    comments: [
      { author_name: "Bob Martinez",  body: "Great intro, very clear!",           created_at: "2026-01-11" },
      { author_name: "Carol Davies",  body: "Thanks, this helped me get started.", created_at: "2026-01-12" }
    ] },
  { _id: 2, author_id: 2,
    title: "REST API Design Tips",
    body: "Consistent naming and versioning matter.",
    published_at: "2026-01-15",
    tags: ["api"],
    comments: [
      { author_name: "Alice Chen", body: "Good points on versioning.", created_at: "2026-01-16" }
    ] },
  { _id: 3, author_id: 1,
    title: "Introduction to SQL Indexing",
    body: "Indexes speed up reads but slow down writes.",
    published_at: "2026-02-01",
    tags: ["sql"],
    comments: [
      { author_name: "Bob Martinez", body: "Composite indexes are particularly tricky.", created_at: "2026-02-02" }
    ] },
  { _id: 4, author_id: 3,
    title: "Testing Strategies for APIs",
    body: "Unit, integration and contract tests all matter.",
    published_at: "2026-02-20",
    tags: ["api", "testing"],
    comments: [
      { author_name: "Alice Chen", body: "Contract tests save a lot of pain.", created_at: "2026-02-21" }
    ] },
  { _id: 5, author_id: 2,
    title: "Twelve-Factor App Revisited",
    body: "A timeless guide for modern cloud services.",
    published_at: "2026-03-05",
    tags: ["devops"],
    comments: [
      { author_name: "Carol Davies", body: "Still relevant after all these years.", created_at: "2026-03-06" }
    ] }
]);

CREATE TABLE authors (
  id    INT          PRIMARY KEY,
  name  VARCHAR(100) NOT NULL,
  email VARCHAR(120)
);
CREATE TABLE posts (
  id           INT           PRIMARY KEY,
  author_id    INT,
  title        VARCHAR(200),
  body         VARCHAR(4000),
  published_at VARCHAR(30)
);
CREATE TABLE blog_comments (
  id          INT           PRIMARY KEY,
  post_id     INT,
  author_name VARCHAR(100),
  body        VARCHAR(2000),
  created_at  VARCHAR(30)
);
CREATE TABLE tags (
  id   INT         PRIMARY KEY,
  name VARCHAR(50)
);
CREATE TABLE post_tags (
  post_id INT,
  tag_id  INT,
  PRIMARY KEY (post_id, tag_id)
);
INSERT INTO authors (id, name, email) VALUES
  (1, 'Alice Chen',    'alice@example.com'),
  (2, 'Bob Martinez',  'bob@example.com'),
  (3, 'Carol Davies',  'carol@example.com');
INSERT INTO posts (id, author_id, title, body, published_at) VALUES
  (1, 1, 'Getting Started with Docker',    'Docker simplifies container workflows.',         '2026-01-10'),
  (2, 2, 'REST API Design Tips',           'Consistent naming and versioning matter.',        '2026-01-15'),
  (3, 1, 'Introduction to SQL Indexing',   'Indexes speed up reads but slow down writes.',    '2026-02-01'),
  (4, 3, 'Testing Strategies for APIs',    'Unit, integration and contract tests all matter.','2026-02-20'),
  (5, 2, 'Twelve-Factor App Revisited',    'A timeless guide for modern cloud services.',     '2026-03-05');
INSERT INTO blog_comments (id, post_id, author_name, body, created_at) VALUES
  (1, 1, 'Bob Martinez',  'Great intro, very clear!',                  '2026-01-11'),
  (2, 1, 'Carol Davies',  'Thanks, this helped me get started.',        '2026-01-12'),
  (3, 2, 'Alice Chen',    'Good points on versioning.',                 '2026-01-16'),
  (4, 3, 'Bob Martinez',  'Composite indexes are particularly tricky.', '2026-02-02'),
  (5, 4, 'Alice Chen',    'Contract tests save a lot of pain.',         '2026-02-21'),
  (6, 5, 'Carol Davies',  'Still relevant after all these years.',      '2026-03-06');
INSERT INTO tags (id, name) VALUES
  (1, 'docker'),
  (2, 'api'),
  (3, 'sql'),
  (4, 'testing'),
  (5, 'devops');
INSERT INTO post_tags (post_id, tag_id) VALUES
  (1, 1), (1, 5),
  (2, 2),
  (3, 3),
  (4, 2), (4, 4),
  (5, 5);

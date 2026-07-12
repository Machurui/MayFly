CREATE TABLE customers (
  id   INT          PRIMARY KEY,
  name VARCHAR(100) NOT NULL,
  city VARCHAR(80)
);
CREATE TABLE products (
  id    INT           PRIMARY KEY,
  name  VARCHAR(100)  NOT NULL,
  price DECIMAL(10,2)
);
CREATE TABLE orders (
  id          INT PRIMARY KEY,
  customer_id INT,
  order_date  VARCHAR(30)
);
CREATE TABLE order_details (
  order_id   INT,
  product_id INT,
  quantity   INT,
  PRIMARY KEY (order_id, product_id)
);
INSERT INTO customers (id, name, city) VALUES
  (1, 'Alfreds Futterkiste', 'Berlin'),
  (2, 'Around the Horn', 'London'),
  (3, 'Bottom-Dollar Markets', 'Vancouver'),
  (4, 'Berglunds snabbkop', 'Lulea'),
  (5, 'Blondel pere et fils', 'Strasbourg'),
  (6, 'Bolido Comidas preparadas', 'Madrid'),
  (7, 'Bon app', 'Marseille'),
  (8, 'Ernst Handel', 'Graz'),
  (9, 'Familia Arquibaldo', 'Sao Paulo'),
  (10, 'FISSA Fabrica Inter. Salchichas', 'Madrid');
INSERT INTO products (id, name, price) VALUES
  (1,  'Chai',                18.00),
  (2,  'Chang',               19.00),
  (3,  'Aniseed Syrup',       10.00),
  (4,  'Chef Anton''s Cajun Seasoning', 22.00),
  (5,  'Grandma''s Boysenberry Spread', 25.00),
  (6,  'Uncle Bob''s Organic Dried Pears', 30.00),
  (7,  'Northwoods Cranberry Sauce', 40.00),
  (8,  'Mishi Kobe Niku',     97.00),
  (9,  'Ikura',               31.00),
  (10, 'Queso Cabrales',      21.00),
  (11, 'Queso Manchego La Pastora', 38.00),
  (12, 'Konbu',                6.00),
  (13, 'Tofu',                23.25),
  (14, 'Genen Shouyu',        15.50),
  (15, 'Pavlova',             17.45);
INSERT INTO orders (id, customer_id, order_date) VALUES
  (1001, 1, '2026-01-10'),
  (1002, 2, '2026-01-12'),
  (1003, 3, '2026-01-15'),
  (1004, 4, '2026-01-20'),
  (1005, 5, '2026-01-22'),
  (1006, 6, '2026-02-01'),
  (1007, 7, '2026-02-03'),
  (1008, 8, '2026-02-05'),
  (1009, 9, '2026-02-10'),
  (1010, 10, '2026-02-14'),
  (1011, 1, '2026-02-18'),
  (1012, 2, '2026-02-20');
INSERT INTO order_details (order_id, product_id, quantity) VALUES
  (1001, 1,  12),
  (1001, 2,  10),
  (1001, 3,   5),
  (1002, 4,   8),
  (1002, 5,   6),
  (1002, 6,   4),
  (1003, 7,   3),
  (1003, 8,   2),
  (1003, 9,   7),
  (1004, 10,  9),
  (1004, 11,  5),
  (1004, 12, 20),
  (1005, 13,  4),
  (1005, 14,  6),
  (1005, 15,  8),
  (1006, 1,  15),
  (1006, 3,  10),
  (1007, 4,   5),
  (1007, 5,   3),
  (1007, 6,   2),
  (1008, 7,   6),
  (1008, 8,   1),
  (1008, 9,  10),
  (1009, 10,  4),
  (1009, 11,  7),
  (1010, 12, 30),
  (1010, 13,  2),
  (1011, 14,  5),
  (1011, 15,  3),
  (1012, 1,   8);

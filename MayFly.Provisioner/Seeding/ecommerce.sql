CREATE TABLE products (
  id       INT           PRIMARY KEY,
  name     VARCHAR(100)  NOT NULL,
  price    DECIMAL(10,2),
  category VARCHAR(60)
);
CREATE TABLE customers (
  id    INT          PRIMARY KEY,
  name  VARCHAR(100) NOT NULL,
  email VARCHAR(120)
);
CREATE TABLE orders (
  id          INT           PRIMARY KEY,
  customer_id INT,
  order_date  VARCHAR(30),
  total       DECIMAL(10,2)
);
CREATE TABLE order_items (
  order_id   INT,
  product_id INT,
  quantity   INT,
  unit_price DECIMAL(10,2),
  PRIMARY KEY (order_id, product_id)
);
INSERT INTO products (id, name, price, category) VALUES
  (1,  'Wireless Headphones',   79.99, 'Electronics'),
  (2,  'Running Shoes',         59.99, 'Footwear'),
  (3,  'Coffee Maker',          49.95, 'Appliances'),
  (4,  'Yoga Mat',              29.99, 'Fitness'),
  (5,  'Bluetooth Speaker',     39.99, 'Electronics'),
  (6,  'Water Bottle',          19.99, 'Fitness'),
  (7,  'Desk Lamp',             24.99, 'Home Office'),
  (8,  'Mechanical Keyboard',   89.99, 'Electronics'),
  (9,  'Notebook Set',          14.99, 'Stationery'),
  (10, 'Sunglasses',            34.99, 'Accessories');
INSERT INTO customers (id, name, email) VALUES
  (1, 'Alice Martin',   'alice@example.com'),
  (2, 'Bob Johnson',    'bob@example.com'),
  (3, 'Carol Williams', 'carol@example.com'),
  (4, 'David Brown',    'david@example.com'),
  (5, 'Eva Garcia',     'eva@example.com');
INSERT INTO orders (id, customer_id, order_date, total) VALUES
  (2001, 1, '2026-01-05', 109.98),
  (2002, 2, '2026-01-08',  59.99),
  (2003, 3, '2026-01-11',  64.94),
  (2004, 4, '2026-01-15',  89.99),
  (2005, 5, '2026-01-20',  69.98),
  (2006, 1, '2026-02-02',  39.99),
  (2007, 2, '2026-02-10',  44.98),
  (2008, 3, '2026-02-14',  24.99);
INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES
  (2001, 1, 1, 79.99),
  (2001, 4, 1, 29.99),
  (2002, 2, 1, 59.99),
  (2003, 3, 1, 49.95),
  (2003, 9, 1, 14.99),
  (2004, 8, 1, 89.99),
  (2005, 4, 1, 29.99),
  (2005, 5, 1, 39.99),
  (2006, 5, 1, 39.99),
  (2007, 6, 1, 19.99),
  (2007, 7, 1, 24.99),
  (2008, 7, 1, 24.99);
